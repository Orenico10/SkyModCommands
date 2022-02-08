using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Coflnet.Sky.Commands.Shared;
using Coflnet.Sky.ModCommands.Dialogs;
using hypixel;
using Newtonsoft.Json;
using WebSocketSharp;

namespace Coflnet.Sky.Commands.MC
{
    /// <summary>
    /// Represents a mod session
    /// </summary>
    public class ModSessionLifesycle
    {
        protected MinecraftSocket socket;
        protected OpenTracing.ITracer tracer => socket.tracer;
        public SessionInfo SessionInfo => socket.SessionInfo;
        public string COFLNET = MinecraftSocket.COFLNET;
        public SelfUpdatingValue<FlipSettings> FlipSettings;
        public SelfUpdatingValue<string> UserId;
        public SelfUpdatingValue<AccountInfo> AccountInfo;
        public OpenTracing.ISpan ConSpan => socket.ConSpan;
        public static FlipSettings DEFAULT_SETTINGS => new FlipSettings()
        {
            MinProfit = 100000,
            MinVolume = 20,
            ModSettings = new ModSettings() { ShortNumbers = true },
            Visibility = new VisibilitySettings() { SellerOpenButton = true, ExtraInfoMax = 3, Lore = true }
        };

        public ModSessionLifesycle(MinecraftSocket socket)
        {
            this.socket = socket;
        }

        public async Task SetupConnectionSettings(string stringId)
        {
            using var loadSpan = socket.tracer.BuildSpan("load").AsChildOf(ConSpan).StartActive();
            /*SettingsChange cachedSettings = null;
            for (int i = 0; i < 3; i++)
            {
                cachedSettings = await CacheService.Instance.GetFromRedis<SettingsChange>(this.Id.ToString());
                if (cachedSettings != null)
                    break;
                await Task.Delay(800); // backoff to give redis time to recover
            }*/
            Console.WriteLine("conId is : " + stringId);
            UserId = await SelfUpdatingValue<string>.Create("mod", stringId);
            if (UserId.Value == default)
            {
                UserId.OnChange += SubToSettings;
                FlipSettings = await SelfUpdatingValue<FlipSettings>.Create("mod", "flipSettings", () => DEFAULT_SETTINGS);
                Console.WriteLine("waiting for load");
            }
            else
                SubToSettings(UserId);

            loadSpan.Span.Finish();
            var index = 1;
            Console.WriteLine("userId: " + UserId.Value);
            while (UserId.Value == null)
            {
                SendMessage(COFLNET + $"Please {McColorCodes.WHITE}§lclick this [LINK] to login {McColorCodes.GRAY}and configure your flip filters §8(you won't receive real time flips until you do)",
                    GetAuthLink(stringId));
                await Task.Delay(TimeSpan.FromSeconds(60 * index++));

                if (UserId != default)
                    return;
                SendMessage("do /cofl stop to stop receiving this (or click this message)", "/cofl stop");
            }
        }

        private void SubToSettings(string val)
        {
            Console.WriteLine("user updated to " + val);
            FlipSettings = SelfUpdatingValue<FlipSettings>.Create(val, "flipSettings", () => DEFAULT_SETTINGS).Result;
            AccountInfo = SelfUpdatingValue<AccountInfo>.Create(val, "accountInfo").Result;

            FlipSettings.OnChange += UpdateSettings;
            Console.WriteLine("assigned default settings");
            AccountInfo.OnChange += (ai) => Task.Run(async () => await UpdateAccountInfo(ai));
            if(AccountInfo.Value != default)
                Task.Run(async () => await  UpdateAccountInfo(AccountInfo));
            else 
                Console.WriteLine("accountinfo is default");
        }

        /// <summary>
        /// Called when setting were updated to apply them
        /// </summary>
        /// <param name="settings"></param>
        private void UpdateSettings(FlipSettings settings)
        {
            using var span = tracer.BuildSpan("SettingsUpdate").AsChildOf(ConSpan.Context)
                    .StartActive();
            var changed = socket.FindWhatsNew(FlipSettings.Value, settings);
            if (string.IsNullOrWhiteSpace(changed))
                changed = "Settings changed";
            SendMessage($"{COFLNET} {changed}");
            if (settings.ModSettings?.Chat ?? false)
                ChatCommand.MakeSureChatIsConnected(socket).Wait();

            if (settings.BasedOnLBin && settings.AllowedFinders != LowPricedAuction.FinderType.SNIPER)
            {
                socket.SendMessage(new DialogBuilder().Msg(McColorCodes.RED + "Your profit is based on lbin, therefore you should only use the `sniper` flip finder to maximise speed"));
            }
            span.Span.Log(JSON.Stringify(settings));
        }

        private async Task UpdateAccountInfo(AccountInfo info)
        {
            using var span = tracer.BuildSpan("AuthUpdate").AsChildOf(ConSpan.Context)
                    .WithTag("premium", info.Tier.ToString())
                    .WithTag("userId", info.UserId.ToString())
                    .StartActive();
            if (info == null)
                return;
            try
            {
                //MigrateSettings(cachedSettings);
                /*ApplySetting(cachedSettings);*/
                UpdateConnectionTier(info, socket.ConSpan);
                var helloTask = SendAuthorizedHello(info);
                SendMessage(socket.formatProvider.WelcomeMessage(),
                    "https://sky.coflnet.com/flipper");
                await Task.Delay(500);
                await helloTask;
                //SendMessage(COFLNET + $"{McColorCodes.DARK_GREEN} click this to relink your account",
                //GetAuthLink(stringId), "You don't need to relink your account. \nThis is only here to allow you to link your mod to the website again should you notice your settings aren't updated");
                return;
            }
            catch (Exception e)
            {
                socket.Error(e, "loading modsocket");
                SendMessage(COFLNET + $"Your settings could not be loaded, please relink again :)");
            }
        }

        private void SendMessage(string message, string click = null, string hover = null)
        {
            socket.SendMessage(message, click, hover);
        }
        private string GetAuthLink(string stringId)
        {
            return $"https://sky.coflnet.com/authmod?mcid={SessionInfo.McName}&conId={HttpUtility.UrlEncode(stringId)}";
        }


        private async Task<OpenTracing.IScope> ModGotAuthorised(AccountInfo settings)
        {
            var span = tracer.BuildSpan("Authorized").AsChildOf(ConSpan.Context).StartActive();
            try
            {
                await SendAuthorizedHello(settings);
                SendMessage($"Authorized connection you can now control settings via the website");
                await Task.Delay(TimeSpan.FromSeconds(20));
                SendMessage($"Remember: the format of the flips is: §dITEM NAME §fCOST -> MEDIAN");
            }
            catch (Exception e)
            {
                socket.Error(e, "settings authorization");
                span.Span.Log(e.Message);
            }

            //await Task.Delay(TimeSpan.FromMinutes(2));
            try
            {
                await CheckVerificationStatus(settings);
            }
            catch (Exception e)
            {
                socket.Error(e, "verification failed");
            }

            return span;
        }

        private async Task CheckVerificationStatus(AccountInfo settings)
        {
            var connect = await McAccountService.Instance.ConnectAccount(settings.UserId.ToString(), SessionInfo.McUuid);
            if (connect.IsConnected)
                return;
            using var verification = tracer.BuildSpan("Verification").AsChildOf(ConSpan.Context).StartActive();
            var activeAuction = await ItemPrices.Instance.GetActiveAuctions(new ActiveItemSearchQuery()
            {
                name = "STICK",
            });
            var bid = connect.Code;
            var r = new Random();

            var targetAuction = activeAuction.Where(a => a.Price < bid).OrderBy(x => r.Next()).FirstOrDefault();
            verification.Span.SetTag("code", bid);
            verification.Span.Log(JSON.Stringify(activeAuction));
            verification.Span.Log(JSON.Stringify(targetAuction));

            socket.SendMessage(new ChatPart(
                $"{COFLNET}You connected from an unkown account. Please verify that you are indeed {SessionInfo.McName} by bidding {McColorCodes.AQUA}{bid}{McCommand.DEFAULT_COLOR} on a random auction.",
                $"/viewauction {targetAuction?.Uuid}",
                $"{McColorCodes.GRAY}Click to open an auction to bid {McColorCodes.AQUA}{bid}{McCommand.DEFAULT_COLOR} on\nyou can also bid another number with the same digits at the end\neg. 1,234,{McColorCodes.AQUA}{bid}"));

        }

        public void UpdateConnectionTier(AccountInfo accountInfo, OpenTracing.ISpan span)
        {
            Console.WriteLine(JsonConvert.SerializeObject(accountInfo));
            this.ConSpan.SetTag("tier", accountInfo.Tier.ToString());
            span.Log("set connection tier to " + accountInfo.Tier.ToString());
            if (DateTime.Now < new DateTime(2022, 1, 22))
            {
                FlipperService.Instance.AddConnection(socket, false);
            }
            else if (accountInfo.Tier == AccountTier.NONE)
            {
                FlipperService.Instance.AddNonConnection(socket, false);
            }
            if ((accountInfo.Tier.HasFlag(AccountTier.PREMIUM) || accountInfo.Tier.HasFlag(AccountTier.STARTER_PREMIUM)) && accountInfo.ExpiresAt > DateTime.Now)
            {
                FlipperService.Instance.AddConnection(socket, false);
            }
            else if (accountInfo.Tier == AccountTier.PREMIUM_PLUS)
                FlipperService.Instance.AddConnectionPlus(socket, false);


        }


        private async Task SendAuthorizedHello(AccountInfo accountInfo)
        {
            var user = UserService.Instance.GetUserById(accountInfo.UserId);
            var length = user.Email.Length < 10 ? 3 : 6;
            var builder = new StringBuilder(user.Email);
            for (int i = 0; i < builder.Length - 5; i++)
            {
                if (builder[i] == '@' || i < 3)
                    continue;
                builder[i] = '*';
            }
            var anonymisedEmail = builder.ToString();
            if (this.SessionInfo.McName == null)
                await Task.Delay(800); // allow another half second for the playername to be loaded
            var messageStart = $"Hello {this.SessionInfo.McName} ({anonymisedEmail}) \n";
            if (accountInfo.Tier != AccountTier.NONE && accountInfo.ExpiresAt > DateTime.Now)
                SendMessage(COFLNET + messageStart + $"You have {accountInfo.Tier.ToString()} until {accountInfo.ExpiresAt}");
            else
                SendMessage(COFLNET + messageStart + $"You use the free version of the flip finder");

            await Task.Delay(300);
        }
    }
}
