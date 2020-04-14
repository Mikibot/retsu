﻿namespace Retsu.Consumer
{
	using Miki.Discord.Common;
	using Miki.Discord.Common.Events;
	using Miki.Discord.Common.Gateway;
	using Miki.Discord.Common.Packets;
	using Miki.Discord.Common.Packets.Events;
	using Miki.Logging;
	using RabbitMQ.Client;
	using RabbitMQ.Client.Events;
	using System;
    using System.Collections.Concurrent;
    using System.Text;
	using System.Threading.Tasks;
    using Miki.Discord.Common.Extensions;
    using Miki.Discord.Common.Packets.API;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Retsu.Models.Communication;

    public partial class RetsuConsumer : IConsumer, IGateway
	{
		public Func<DiscordChannelPacket, Task> OnChannelCreate { get; set; }
		public Func<DiscordChannelPacket, Task> OnChannelUpdate { get; set; }
		public Func<DiscordChannelPacket, Task> OnChannelDelete { get; set; }
		public Func<DiscordGuildPacket, Task> OnGuildCreate { get; set; }
		public Func<DiscordGuildPacket, Task> OnGuildUpdate { get; set; }
		public Func<DiscordGuildUnavailablePacket, Task> OnGuildDelete { get; set; }
		public Func<DiscordGuildMemberPacket, Task> OnGuildMemberAdd { get; set; }
		public Func<ulong, DiscordUserPacket, Task> OnGuildMemberRemove { get; set; }
		public Func<GuildMemberUpdateEventArgs, Task> OnGuildMemberUpdate { get; set; }
		public Func<ulong, DiscordUserPacket, Task> OnGuildBanAdd { get; set; }
		public Func<ulong, DiscordUserPacket, Task> OnGuildBanRemove { get; set; }
		public Func<ulong, DiscordEmoji[], Task> OnGuildEmojiUpdate { get; set; }
		public Func<ulong, DiscordRolePacket, Task> OnGuildRoleCreate { get; set; }
		public Func<ulong, DiscordRolePacket, Task> OnGuildRoleUpdate { get; set; }
		public Func<ulong, ulong, Task> OnGuildRoleDelete { get; set; }
		public Func<DiscordMessagePacket, Task> OnMessageCreate { get; set; }
		public Func<DiscordMessagePacket, Task> OnMessageUpdate { get; set; }
		public Func<MessageDeleteArgs, Task> OnMessageDelete { get; set; }
		public Func<MessageBulkDeleteEventArgs, Task> OnMessageDeleteBulk { get; set; }
		public Func<DiscordPresencePacket, Task> OnPresenceUpdate { get; set; }
		public Func<GatewayReadyPacket, Task> OnReady { get; set; }
		public Func<TypingStartEventArgs, Task> OnTypingStart { get; set; }
		public Func<DiscordPresencePacket, Task> OnUserUpdate { get; set; }
        public Func<GatewayMessage, Task> OnPacketSent { get; set; }
        public Func<GatewayMessage, Task> OnPacketReceived { get; set; }

        private readonly IModel channel;

        private readonly ConcurrentDictionary<string, EventingBasicConsumer> consumers
            = new ConcurrentDictionary<string, EventingBasicConsumer>();

		private readonly ConsumerConfiguration config;

		public RetsuConsumer(ConsumerConfiguration config)
		{
            this.config = config;

            ConnectionFactory connectionFactory = new ConnectionFactory
            {
                Uri = config.ConnectionString,
                DispatchConsumersAsync = false
            };

            var connection = connectionFactory.CreateConnection();

			connection.CallbackException += (s, args) =>
			{
				Log.Error(args.Exception);
			};

			connection.ConnectionRecoveryError += (s, args) =>
			{
				Log.Error(args.Exception);
			};

			connection.RecoverySucceeded += (s, args) =>
			{
				Log.Debug("Rabbit Connection Recovered!");
			};

			channel = connection.CreateModel();
			channel.BasicQos(config.PrefetchSize, config.PrefetchCount, false);
			channel.ExchangeDeclare(config.ExchangeName, ExchangeType.Direct);
			channel.QueueDeclare(config.QueueName, config.QueueDurable, config.QueueExclusive, config.QueueAutoDelete, null);
			channel.QueueBind(config.QueueName, config.ExchangeName, config.ExchangeRoutingKey, null);

			var commandChannel = connectionFactory.CreateConnection().CreateModel();
			commandChannel.ExchangeDeclare(
                config.QueueName + "-command", ExchangeType.Fanout, true);
			commandChannel.QueueDeclare(
                config.QueueName + "-command", false, false, false);
			commandChannel.QueueBind(
                config.QueueName + "-command", 
                config.QueueName + "-command", 
                config.ExchangeRoutingKey, null);
		}

		public async Task RestartAsync()
		{
			await StopAsync();
			await StartAsync();
		}

		public Task StartAsync()
		{
			var consumer = new EventingBasicConsumer(channel);
			consumer.Received += async (ch, ea) => await OnMessageAsync(ch, ea);

			// TODO: remove once transition is complete.
			string _ = channel.BasicConsume(
                config.QueueName, config.ConsumerAutoAck, consumer);
            consumers.TryAdd("", consumer);

			return Task.CompletedTask;
		}

		public Task StopAsync()
		{
			return Task.CompletedTask;
		}

		private async Task OnMessageAsync(object ch, BasicDeliverEventArgs ea)
		{
			var payload = Encoding.UTF8.GetString(ea.Body);
			var body = JsonConvert.DeserializeObject<GatewayMessage>(payload);

			if(body.OpCode != GatewayOpcode.Dispatch)
			{
				channel.BasicAck(ea.DeliveryTag, false);
				Log.Trace("packet from gateway with op '" + body.OpCode + "' received");
				return;
			}

			try
			{
				Log.Trace("packet with the op-code '" + body.EventName + "' received.");
				switch(Enum.Parse(typeof(GatewayEventType), body.EventName.Replace("_", ""), true))
				{
					case GatewayEventType.MessageCreate:
					{
						if(OnMessageCreate != null)
						{
							await OnMessageCreate(
                                (body.Data as JToken).ToObject<DiscordMessagePacket>());
						}
					}
					break;

					case GatewayEventType.GuildCreate:
					{
						if(OnGuildCreate != null)
						{
							var guild = (body.Data as JToken).ToObject<DiscordGuildPacket>();

							await OnGuildCreate(
								guild
							);
						}
					}
					break;

					case GatewayEventType.ChannelCreate:
					{
						if(OnGuildCreate != null)
						{
							var discordChannel = (body.Data as JToken).ToObject<DiscordChannelPacket>();

							await OnChannelCreate(discordChannel);
						}
					}
					break;

					case GatewayEventType.GuildMemberRemove:
					{
						if(OnGuildMemberRemove != null)
                        {
                            var packet = (body.Data as JToken).ToObject<GuildIdUserArgs>();

							await OnGuildMemberRemove(
								packet.guildId,
								packet.user
							);
						}
					}
					break;

					case GatewayEventType.GuildMemberAdd:
					{
						DiscordGuildMemberPacket guildMember 
                            = (body.Data as JToken).ToObject<DiscordGuildMemberPacket>();

						if(OnGuildMemberAdd != null)
						{
							await OnGuildMemberAdd(guildMember);
						}
					}
					break;

					case GatewayEventType.GuildMemberUpdate:
					{
						GuildMemberUpdateEventArgs guildMember =
                            (body.Data as JToken).ToObject<GuildMemberUpdateEventArgs>();

						if(OnGuildMemberUpdate != null)
						{
							await OnGuildMemberUpdate(
								guildMember
							);
						}
					}
					break;

					case GatewayEventType.GuildRoleCreate:
					{
						RoleEventArgs role = (body.Data as JToken).ToObject<RoleEventArgs>();

						if(OnGuildRoleCreate != null)
						{
							await OnGuildRoleCreate(
								role.GuildId,
								role.Role
							);
						}
					}
					break;

					case GatewayEventType.GuildRoleDelete:
					{
						if(OnGuildRoleDelete != null)
						{
							RoleDeleteEventArgs role = (body.Data as JToken)
                                .ToObject<RoleDeleteEventArgs>();

							await OnGuildRoleDelete(
								role.GuildId,
								role.RoleId
							);
						}
					}
					break;

					case GatewayEventType.GuildRoleUpdate:
					{
						RoleEventArgs role = (body.Data as JToken).ToObject<RoleEventArgs>();

						if(OnGuildRoleUpdate != null)
						{
							await OnGuildRoleUpdate(
								role.GuildId,
								role.Role
							);
						}
					}
					break;

					case GatewayEventType.ChannelDelete:
					{
						if(OnChannelDelete != null)
						{
							await OnChannelDelete(
                                (body.Data as JToken).ToObject<DiscordChannelPacket>());
						}
					}
					break;

					case GatewayEventType.ChannelUpdate:
					{
						if(OnChannelUpdate != null)
						{
							await OnChannelUpdate(
                                (body.Data as JToken).ToObject<DiscordChannelPacket>());
						}
					}
					break;

					case GatewayEventType.GuildBanAdd:
					{
						if(OnGuildBanAdd != null)
						{
							var packet = (body.Data as JToken).ToObject<GuildIdUserArgs>();

							await OnGuildBanAdd(
								packet.guildId,
								packet.user
							);
						}
					}
					break;

					case GatewayEventType.GuildBanRemove:
					{
						if(OnGuildBanRemove != null)
						{
							var packet = (body.Data as JToken).ToObject<GuildIdUserArgs>();

							await OnGuildBanRemove(
								packet.guildId,
								packet.user
							);
						}
					}
					break;

					case GatewayEventType.GuildDelete:
					{
						if(OnGuildDelete != null)
						{
							var packet = (body.Data as JToken)
                                .ToObject<DiscordGuildUnavailablePacket>();

							await OnGuildDelete(
								packet
							);
						}
					}
					break;

					case GatewayEventType.GuildEmojisUpdate:
					{
						if(OnGuildEmojiUpdate != null)
                        {
                            var packet = (body.Data as JToken).ToObject<GuildEmojisUpdateEventArgs>();

							await OnGuildEmojiUpdate(
								packet.guildId,
								packet.emojis
							);
						}
					}
					break;

					case GatewayEventType.GuildIntegrationsUpdate:
					{
					}
					break;

					case GatewayEventType.GuildMembersChunk:
					{
					}
					break;

					case GatewayEventType.GuildUpdate:
					{
                        if(OnGuildUpdate != null)
                        {
                            await OnGuildUpdate(
                                (body.Data as JToken).ToObject<DiscordGuildPacket>());
                        }
                    }
					break;

					case GatewayEventType.MessageDelete:
					{
						if(OnMessageDelete != null)
                        {
                            await OnMessageDelete(
                                (body.Data as JToken).ToObject<MessageDeleteArgs>());
                        }
					}
					break;

					case GatewayEventType.MessageDeleteBulk:
					{
						if(OnMessageDeleteBulk != null)
                        {
                            await OnMessageDeleteBulk(
                                (body.Data as JToken).ToObject<MessageBulkDeleteEventArgs>());
                        }
					}
					break;

					case GatewayEventType.MessageUpdate:
					{
						if(OnMessageUpdate != null)
                        {
                            await OnMessageUpdate(
                                (body.Data as JToken).ToObject<DiscordMessagePacket>());
                        }
					}
					break;

					case GatewayEventType.PresenceUpdate:
					{
						if(OnPresenceUpdate != null)
                        {
                            await OnPresenceUpdate(
                                (body.Data as JToken).ToObject<DiscordPresencePacket>());
                        }
					}
					break;

					case GatewayEventType.Ready:
					{
							OnReady.InvokeAsync(
								(body.Data as JToken).ToObject<GatewayReadyPacket>()
							).Wait();
					}

					break;

					case GatewayEventType.Resumed:
					{

					}
					break;

					case GatewayEventType.TypingStart:
					{
						if(OnTypingStart != null)
                        {
                            await OnTypingStart(
                                (body.Data as JToken).ToObject<TypingStartEventArgs>());
                        }
					}
					break;

					case GatewayEventType.UserUpdate:
					{
						if(OnUserUpdate != null)
                        {
                            await OnUserUpdate(
                                (body.Data as JToken).ToObject<DiscordPresencePacket>());
                        }
					}
					break;

					case GatewayEventType.VoiceServerUpdate:
					{
					}
					break;

					case GatewayEventType.VoiceStateUpdate:
					{
					}
					break;
				}

				if(!config.ConsumerAutoAck)
				{
					channel.BasicAck(ea.DeliveryTag, false);
				}
			}
			catch(Exception e)
			{
				Log.Error(e);

				if(!config.ConsumerAutoAck)
				{
					channel.BasicNack(ea.DeliveryTag, false, false);
				}
			}
		}

		public System.Threading.Tasks.Task SendAsync(int shardId, GatewayOpcode opcode, object payload)
		{
            CommandMessage msg = new CommandMessage
            {
                Opcode = opcode,
                ShardId = shardId,
                Data = payload
            };

            channel.BasicPublish(
                "gateway-command", "", body: Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(msg)));
			return System.Threading.Tasks.Task.CompletedTask;
		}

        /// <inheritdoc />
        public ValueTask SubscribeAsync(string ev)
        {
            var key = config.QueueName + ":" + ev;
            if(consumers.ContainsKey(key))
            {
                throw new InvalidOperationException("Queue already subscribed");
            }

			var consumer = new EventingBasicConsumer(channel);
            consumer.Received += async (ch, ea) => await OnMessageAsync(ch, ea);

            channel.QueueDeclare(key, true, false, false);
            channel.QueueBind(key, config.ExchangeName, ev);

			string _ = channel.BasicConsume(
                key, config.ConsumerAutoAck, consumer);
            consumers.TryAdd("", consumer);

			return default;
        }

        /// <inheritdoc />
        public ValueTask UnsubscribeAsync(string ev)
        {
			return default;
        }
    }
}