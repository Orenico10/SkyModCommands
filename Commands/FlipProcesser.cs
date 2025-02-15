using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Commands.Shared;
using Coflnet.Sky.Core;
using Coflnet.Sky.ModCommands.Services;
using Newtonsoft.Json;
using OpenTracing;

namespace Coflnet.Sky.Commands.MC
{
    public class FlipProcesser
    {
        private static Prometheus.Counter sentFlipsCount = Prometheus.Metrics.CreateCounter("sky_mod_sent_flips", "How many flip messages were sent");
        private static Prometheus.Histogram flipSendTiming = Prometheus.Metrics.CreateHistogram("sky_mod_send_time", "Full run through time of flips");

        private ConcurrentDictionary<long, DateTime> SentFlips = new ConcurrentDictionary<long, DateTime>();
        protected MinecraftSocket socket;
        private FlipSettings Settings => socket.Settings;
        private SpamController spamController;
        private DelayHandler delayHandler;
        private int waitingBedFlips = 0;
        private int _blockedFlipCounter = 0;
        public int BlockedFlipCount => _blockedFlipCounter;

        public FlipProcesser(MinecraftSocket socket, SpamController spamController, DelayHandler delayHandler)
        {
            this.socket = socket;
            this.spamController = spamController;
            this.delayHandler = delayHandler;
        }

        public async Task NewFlips(IEnumerable<LowPricedAuction> flips)
        {
            if (AreFlipsDisabled())
                return;

            var prefiltered = flips.Where(f => !SentFlips.ContainsKey(f.UId)
                && FinderEnabled(f)
                && NotSold(f))
                .Select(f => (f, instance: FlipperService.LowPriceToFlip(f)))
                .ToList();

            if (!Settings.FastMode && (Settings.BasedOnLBin || ((Settings.Visibility?.LowestBin ?? false) || (Settings.Visibility?.Seller ?? false))))
            {
                await LoadAdditionalInfo(prefiltered).ConfigureAwait(false);
            }

            var matches = prefiltered.Where(f => FlipMatchesSetting(f.f, f.instance)
                && IsNoDupplicate(f.f)).ToList();
            if (matches.Count == 0)
                return;

            using var span = socket.tracer.BuildSpan("Flip")
                .WithTag("uuid", matches.First().f.Auction.Uuid)
                .WithTag("batchSize", matches.Count)
                .AsChildOf(socket.ConSpan.Context).StartActive();

            var toSend = matches.Where(f => NotBlockedForSpam(f.instance, f.f, span)).ToList();
            foreach (var item in toSend)
            {

                var timeToSend = DateTime.UtcNow - item.f.Auction.FindTime;
                item.f.AdditionalProps["dl"] = (timeToSend).ToString();
            }

            await SendAfterDelay(toSend.ToList());

            while (SentFlips.Count > 700)
            {
                foreach (var item in SentFlips.Where(i => i.Value < DateTime.UtcNow - TimeSpan.FromMinutes(2)).ToList())
                {
                    SentFlips.TryRemove(item.Key, out DateTime value);
                }
            }
            while (socket.LastSent.Count > 30)
                socket.LastSent.TryDequeue(out _);
        }

        private async Task LoadAdditionalInfo(List<(LowPricedAuction f, FlipInstance instance)> prefiltered)
        {
            foreach (var flipSum in prefiltered)
            {
                var flipInstance = flipSum.instance;
                Settings.GetPrice(flipInstance, out _, out long profit);
                if (!Settings.BasedOnLBin && Settings.MinProfit > profit)
                    continue;
                try
                {
                    await FlipperService.FillVisibilityProbs(flipInstance, Settings).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    socket.Error(e, "filling visibility");
                }
            }
        }

        private bool AreFlipsDisabled()
        {
            return this.Settings == null || this.Settings.DisableFlips;
        }

        private bool NotBlockedForSpam(FlipInstance flipInstance, LowPricedAuction f, IScope span)
        {
            if (spamController.ShouldBeSent(flipInstance))
                return true;
            return BlockedFlip(f, "spam");
        }

        private bool IsNoDupplicate(LowPricedAuction flip)
        {
            // this check is down here to avoid filling up the list
            if (SentFlips.TryAdd(flip.UId, DateTime.UtcNow))
                return true; // make sure flips are not sent twice
            return false;
        }

        private bool FlipMatchesSetting(LowPricedAuction flip, FlipInstance flipInstance)
        {
            var isMatch = (false, "");
            try
            {
                isMatch = Settings.MatchesSettings(flipInstance);
                if (flip.AdditionalProps == null)
                    flip.AdditionalProps = new Dictionary<string, string>();
                flip.AdditionalProps["match"] = isMatch.Item2;
                if (isMatch.Item2.StartsWith("whitelist"))
                    flipInstance.Interesting.Insert(0, "WL");
            }
            catch (Exception e)
            {
                var id = socket.Error(e, "matching flip settings", JSON.Stringify(flip) + "\n" + JSON.Stringify(Settings));
                dev.Logger.Instance.Error(e, "minecraft socket flip settings matching " + id);
                return BlockedFlip(flip, "Error " + e.Message);
            }
            if (Settings != null && !isMatch.Item1)
                return BlockedFlip(flip, isMatch.Item2);
            return true;
        }

        private bool NotSold(LowPricedAuction flip)
        {
            if (flip.AdditionalProps?.ContainsKey("sold") ?? false)
                return BlockedFlip(flip, "sold");
            else
                return true;
        }

        private bool FinderEnabled(LowPricedAuction flip)
        {
            if (Settings.IsFinderBlocked(flip.Finder))
                if (flip.Finder == LowPricedAuction.FinderType.USER)
                    return false;
                else
                    return BlockedFlip(flip, "finder " + flip.Finder);
            else
                return true;
        }

        private async Task SendAfterDelay(IEnumerable<(Coflnet.Sky.Core.LowPricedAuction f, Coflnet.Sky.Commands.Shared.FlipInstance instance)> flips)
        {
            var flipsWithTime = flips.Select(f => (f.instance, f.f.Auction.Start + TimeSpan.FromSeconds(20) - DateTime.UtcNow, lp: f.f));
            var bedsToWaitFor = flipsWithTime.Where(f => f.Item2 > TimeSpan.FromSeconds(3.1) && !(Settings?.ModSettings.NoBedDelay ?? false));
            var noBed = flipsWithTime.ExceptBy(bedsToWaitFor.Select(b => b.lp.Auction.Uuid), b => b.lp.Auction.Uuid).Select(f => (f.instance, f.lp));
            var toSendInstant = noBed.Where(f => delayHandler.IsLikelyBot(f.instance)).ToList();
            foreach (var item in flips)
            {
                flipSendTiming.Observe((DateTime.UtcNow - item.f.Auction.FindTime).TotalSeconds);
            }

            foreach (var item in toSendInstant)
            {
                await SendAndTrackFlip(item.instance, item.lp, DateTime.UtcNow).ConfigureAwait(false);
            }
            var toSendDelayed = noBed.ExceptBy(toSendInstant.Select(b => b.lp.Auction.Uuid), b => b.lp.Auction.Uuid);
            await NewMethod(noBed, toSendDelayed).ConfigureAwait(false);

            // beds
            foreach (var item in bedsToWaitFor.OrderBy(b => b.Item2))
            {
                if (socket.sessionLifesycle.CurrentDelay > TimeSpan.FromSeconds(0.6))
                {
                    await Task.Delay(item.Item2).ConfigureAwait(false);
                    await SendAndTrackFlip(item.instance, item.lp, DateTime.UtcNow).ConfigureAwait(false);
                    continue;
                }
                await WaitForBedToSend(item).ConfigureAwait(false);

            }
        }

        private async Task NewMethod(IEnumerable<(FlipInstance instance, LowPricedAuction lp)> noBed, IEnumerable<(FlipInstance instance, LowPricedAuction lp)> toSendDelayed)
        {
            var bestFlip = noBed.Select(f => f.instance).MaxBy(f => f.Profit);
            if (bestFlip == null)
                return;

            var sendTime = await delayHandler.AwaitDelayForFlip(bestFlip);
            foreach (var item in toSendDelayed)
            {
                await SendAndTrackFlip(item.instance, item.lp, sendTime).ConfigureAwait(false);
            }
        }

        private async Task WaitForBedToSend((FlipInstance instance, TimeSpan, LowPricedAuction lp) item)
        {
            Interlocked.Increment(ref waitingBedFlips);
            var flip = item.instance;
            var endsIn = flip.Auction.Start + TimeSpan.FromSeconds(17) - DateTime.UtcNow;
            socket.sessionLifesycle.StartTimer(endsIn.TotalSeconds, McColorCodes.GREEN + "Bed in: §c");
            socket.SendSound("note.bass");
            await Task.Delay(endsIn).ConfigureAwait(false);
            Interlocked.Decrement(ref waitingBedFlips);
            if (waitingBedFlips == 0)
            {
                socket.sessionLifesycle.StartTimer(0, "clear timer");
                socket.SheduleTimer();
            }
            // update interesting props because the bed time is different now
            flip.Interesting = Helper.PropertiesSelector.GetProperties(flip.Auction)
                            .OrderByDescending(a => a.Rating).Select(a => a.Value).ToList();

            await SendAndTrackFlip(flip, item.lp, DateTime.UtcNow).ConfigureAwait(false);
        }

        private async Task SendAndTrackFlip(FlipInstance item, LowPricedAuction flip, DateTime sendTime)
        {
            await socket.ModAdapter.SendFlip(item).ConfigureAwait(false);

            _ = socket.TryAsyncTimes(async () =>
            {

                // this is actually syncronous
                await socket.GetService<FlipTrackingService>()
                    .ReceiveFlip(item.Auction.Uuid, socket.sessionLifesycle.SessionInfo.McUuid, sendTime);

                var timeToSend = DateTime.UtcNow - item.Auction.FindTime;
                flip.AdditionalProps["csend"] = (timeToSend).ToString();

                socket.LastSent.Enqueue(flip);
                sentFlipsCount.Inc();

                socket.sessionLifesycle.PingTimer.Change(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(55));

                if (timeToSend > TimeSpan.FromSeconds(15) && socket.AccountInfo?.Tier >= AccountTier.PREMIUM 
                    && flip.Finder != LowPricedAuction.FinderType.FLIPPER && !(item.Interesting.FirstOrDefault()?.StartsWith("Bed") ?? false))
                {
                    // very bad, this flip was very slow, create a report
                    using var slowSpan = socket.tracer.BuildSpan("slowFlip").AsChildOf(socket.ConSpan).WithTag("error", true).StartActive();
                    slowSpan.Span.Log(JsonConvert.SerializeObject(flip.Auction.Context));
                    slowSpan.Span.Log(JsonConvert.SerializeObject(flip.AdditionalProps));
                    foreach (var snapshot in SnapShotService.Instance.SnapShots)
                    {
                        slowSpan.Span.Log(snapshot.Time + " " + snapshot.State);
                    }
                    ReportCommand.TryAddingAllSettings(slowSpan);
                }
            }, "tracking flip");
        }

        /// <summary>
        /// Sends a new flip after delaying to account for macro/ping advantage
        /// </summary>
        /// <param name="flipInstance"></param>
        /// <returns></returns>
        private async Task<Task> SendAfterDelay(FlipInstance flipInstance)
        {

            throw new Exception();
            /*if (SessionInfo.LastSpeedUpdate < DateTime.UtcNow - TimeSpan.FromSeconds(50))
            {
                var adjustment = MinecraftSocket.NextFlipTime - DateTime.UtcNow - TimeSpan.FromSeconds(60);
                if (Math.Abs(adjustment.TotalSeconds) < 1)
                    SessionInfo.RelativeSpeed = adjustment;
                SessionInfo.LastSpeedUpdate = DateTime.UtcNow;
            }*/

        }


        private bool BlockedFlip(LowPricedAuction flip, string reason)
        {
            socket.TopBlocked.Enqueue(new()
            {
                Flip = flip,
                Reason = reason
            });
            Interlocked.Increment(ref _blockedFlipCounter);
            return false;
        }

        /// <summary>
        /// Has to be execute once a minute to clean up state 
        /// </summary>
        public void MinuteCleanup()
        {
            _blockedFlipCounter = 0;
            spamController.Reset();
            Console.WriteLine("housekeeping");
        }
    }
}
