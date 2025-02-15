using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Castle.Core.Configuration;
using Coflnet.Sky.Commands;
using Coflnet.Sky.Commands.MC;
using Coflnet.Sky.Commands.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.ModCommands.Tests;

public class MinecraftSocketTests
{
    [Test]
    [TestCase(11, 60, 11)]
    [TestCase(5, 5, 5)]
    [TestCase(51, 60, 51)]
    public async Task TestTimer(int updateIn, int countdown, int expected)
    {
        var mockSocket = new Mock<MinecraftSocket>();
        mockSocket.Setup(s => s.GetService<FlipTrackingService>()).Returns(new FlipTrackingService(null, null, null));
        var session = new Mock<ModSessionLifesycle>(mockSocket.Object);
        session.Setup(s => s.StartTimer(It.IsAny<int>(), It.IsAny<string>()));
        var socket = new TestSocket(session.Object);
        socket.SetNextFlipTime(DateTime.UtcNow + TimeSpan.FromSeconds(updateIn));
        socket.SheduleTimer(new Commands.Shared.ModSettings() { TimerSeconds = countdown });
        await Task.Delay(10).ConfigureAwait(false);
        session.Verify(s => s.StartTimer(It.Is<double>(v => Math.Round(v, 1) == expected), It.IsAny<string>()), Times.Once);
    }

    public class TestSocket : MinecraftSocket
    {
        public void SetNextFlipTime(DateTime time)
        {
            NextFlipTime = time;
        }
        public TestSocket(ModSessionLifesycle session)
        {
            this.sessionLifesycle = session;
        }
    }
}

public class FlipStreamTests
{
    //[Test]
    public async Task LoadTest()
    {
        IServiceCollection collection = new ServiceCollection();
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string>() { { "API_BASE_URL", "http://no" } });
        collection.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>((a) => builder.Build());
        collection.AddLogging();
        collection.AddCoflService();
        collection.AddSingleton<GemPriceService, MockGemService>();
        collection.AddOpenTracing();
        var socket = new MinecraftSocket();
        socket.SetLifecycleVersion("1.4.2-Alpha");
        socket.sessionLifesycle.FlipSettings = await SelfUpdatingValue<FlipSettings>.CreateNoUpdate(() => new FlipSettings());
        socket.sessionLifesycle.AccountInfo = await SelfUpdatingValue<AccountInfo>.CreateNoUpdate(() => new AccountInfo(){Tier = AccountTier.PREMIUM});
        FlipperService.Instance.AddConnection(socket);

        //_ = Task.Run(async () =>
       // {
            for (int i = 0; i < 1000; i++)
            {
                await FlipperService.Instance.DeliverLowPricedAuction(new Core.LowPricedAuction() { Auction = new(), Finder = Core.LowPricedAuction.FinderType.SNIPER });
            }
       // });
        await Task.Delay(10);
        Assert.GreaterOrEqual(socket.TopBlocked.Count, 500);

    }

    public class MockGemService : GemPriceService
    {
        public MockGemService(Microsoft.Extensions.Configuration.IConfiguration config, IServiceScopeFactory scopeFactory, ILogger<GemPriceService> logger, Microsoft.Extensions.Configuration.IConfiguration configuration) : base(config, scopeFactory, logger, configuration)
        {
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {// break;
            return Task.CompletedTask;
        }
    }
}
