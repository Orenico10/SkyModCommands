using System;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using MessagePack;
using Newtonsoft.Json;
using StackExchange.Redis;
using Coflnet.Sky.Commands.MC;
using Coflnet.Sky.Chat.Client.Client;
using Microsoft.Extensions.Configuration;
using Coflnet.Sky.Chat.Client.Model;
using Coflnet.Sky.Commands.Shared;
using OpenTracing;

namespace Coflnet.Sky.ModCommands.Services;

public class ChatService
{
    Chat.Client.Api.ChatApi api;
    string chatAuthKey;

    public ChatService(IConfiguration config)
    {
        api = new(config["CHAT:BASE_URL"]);
        chatAuthKey = config["CHAT:API_KEY"];
    }
    public async Task<ChannelMessageQueue> Subscribe(Func<ChatMessage, bool> OnMessage)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var sub = await GetCon().SubscribeAsync("chat");

                sub.OnMessage((value) =>
                {
                    var message = JsonConvert.DeserializeObject<ChatMessage>(value.Message);
                    if (!OnMessage(message))
                        sub.Unsubscribe();
                });
                return sub;
            }
            catch (Exception)
            {
                if (i >= 2)
                    throw;
                await Task.Delay(300).ConfigureAwait(false);
            }
        }
        throw new CoflnetException("connection_failed", "connection to chat failed");
    }

    public async Task Send(ModChatMessage message, ISpan span)
    {
        try
        {
            var chatMsg = new Chat.Client.Model.ChatMessage(
                message.SenderUuid, message.SenderName,
                message.Tier switch
                {
                    AccountTier.PREMIUM_PLUS => McColorCodes.GOLD,
                    AccountTier.PREMIUM => McColorCodes.DARK_GREEN,
                    AccountTier.STARTER_PREMIUM => McColorCodes.WHITE,
                    _ => McColorCodes.GRAY
                },
                message.Message);
            span.Log("sending to service");
            await api.ApiChatSendPostAsync(chatAuthKey, chatMsg);
            return;
        }
        catch (ApiException e)
        {
            throw JsonConvert.DeserializeObject<CoflnetException>(e.Message?.Replace("Error calling ApiChatSendPost: ", ""));
        }
    }

    public async Task Mute(Mute mute)
    {
        await api.ApiChatMutePostAsync(chatAuthKey, mute);
    }

    [MessagePackObject]
    public class ModChatMessage
    {
        [Key(0)]
        public string SenderName;
        [Key(1)]
        public string Message;
        [Key(2)]
        public AccountTier Tier;
        [Key(3)]
        public string SenderUuid;
    }

    private static ISubscriber GetCon()
    {
        return CacheService.Instance.RedisConnection.GetSubscriber();
    }
}

