using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using Meebey.SmartIrc4net;
using Newtonsoft.Json;

using Discord;
using Discord.WebSocket;
using Discord.Webhook;

namespace CornerIRCRelay
{
    class Program
    {
        static void Main(string[] args) => new Program().Run().GetAwaiter().GetResult();
        private static readonly HttpClient client = new HttpClient();

        static RelayConfig config;
        
        // IRC
        static IrcClient irc = new IrcClient();

        // Discord
        static DiscordSocketClient discord;
        static SocketGuild guild;
        static Dictionary<string, SocketTextChannel> discordChannels = new Dictionary<string, SocketTextChannel>();
        static Dictionary<string, DiscordWebhookClient> discordWebhooks = new Dictionary<string, DiscordWebhookClient>();
        static Dictionary<string, ulong> discordWebhookIDs = new Dictionary<string, ulong>();
        static bool isDiscordReady;

        // IRC Color and formatting codes (PIRCH98 style)
        private const char IRC_BOLD = '\x02';
        private const char IRC_ITALIC = '\x1D';
        private const char IRC_ITALIC_PIRCH98 = '\x16';  // PIRCH98-specific italic
        private const char IRC_UNDERLINE = '\x1F';
        private const char IRC_STRIKETHROUGH = '\x1E';
        private const char IRC_COLOR = '\x03';
        private const char IRC_RESET = '\x0F';

        public async Task Run()
        {
            // Configure HttpClient with timeout to prevent hanging requests
            client.Timeout = TimeSpan.FromSeconds(10);
            
            // Load the config
            Console.WriteLine("Loading configuration...");
            if (File.Exists("config.json"))
            {
                string json = File.ReadAllText("config.json");
                config = JsonConvert.DeserializeObject<RelayConfig>(json);
                Console.WriteLine("Configuration loaded!");
            }
            else
            {
                // Create a blank config
                config = new RelayConfig();
                File.WriteAllText("config.json", JsonConvert.SerializeObject(config, Formatting.Indented));
                Console.WriteLine("No configuration file found! A blank config has been written to config.json");
                return;
            }

            // Connect to Discord
            Console.WriteLine("Connecting to Discord...");
            var socketConfig = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
            };

            discord = new DiscordSocketClient(socketConfig);
            discord.Log += (LogMessage msg) =>
            {
                Console.WriteLine("Discord.NET: " + msg.Message);
                return Task.CompletedTask;
            };
            discord.Ready += () =>
            {
                isDiscordReady = true;
                return Task.CompletedTask;
            };
            discord.MessageReceived += OnDiscordMessage;
            discord.MessageUpdated += OnMessageUpdated;

            await discord.LoginAsync(TokenType.Bot, config.discordToken);
            await discord.StartAsync();

            while (!isDiscordReady) 
            {
                await Task.Delay(500); // Short pause until Discord is ready
            }

            // Check for the Discord server and channels
            guild = discord.GetGuild(config.discordServerId);
            if (guild == null)
            {
                Console.WriteLine("Unable to find the specified Discord server! Check your configuration.");
                return;
            }
            else
            {
                Console.WriteLine("Successfully found Discord server: " + guild.Name);
            }

            // Set up channels or webhooks from config
            foreach (var channel in config.channels)
            {
                SocketTextChannel discordChannel = (SocketTextChannel)discord.GetChannel(channel.Value);
                if (discordChannel != null)
                {
                    Console.WriteLine("Found Discord channel: " + discordChannel.Name);

                    if (config.formatting.useWebhooks)
                    {
                        // Get or create the webhook
                        var webhook = discordChannel.GetWebhooksAsync().Result.FirstOrDefault(x => x.Name == "Shitcord-IRC-Relay");
                        if (webhook == null)
                            webhook = await discordChannel.CreateWebhookAsync("Shitcord-IRC-Relay");
                        
                        // Add the webhook
                        discordWebhooks.Add(channel.Key.ToLower(), new DiscordWebhookClient(webhook));
                        discordWebhookIDs.Add(channel.Key.ToLower(), webhook.Id);
                    }
                    else
                    {
                        discordChannels.Add(channel.Key, discordChannel);
                    }
                }
                else
                {
                    Console.WriteLine("Could not find Discord channel: " + channel.Value);
                }
            }

            // Connect to IRC server
            Console.WriteLine("Connecting to IRC...");
            irc.Connect(config.IRCIp, config.IRCport);

            // Log in with the desired nickname
            irc.Login(config.IRCNickname, config.IRCNickname);

            // Authenticate with NickServ before joining channels
            if (config.useNickServ && !string.IsNullOrWhiteSpace(config.nickServPass))
                irc.SendMessage(SendType.Message, "NickServ", $"IDENTIFY {config.nickServPass}");

            // Now join the IRC channels
            foreach (var channel in config.channels)
            {
                irc.RfcJoin(channel.Key);
                Console.WriteLine($"Joined IRC channel: {channel.Key}");
            }

            // Listen for messages
            irc.OnChannelMessage += OnIRCMessage;
            irc.CtcpVersion = "CornerIRCRelay version 1.3";
            Console.WriteLine("Connected!");
            irc.Listen(true);

            await Task.Delay(-1);
        }

        // Convert Discord markdown to IRC formatting codes
        private static string ConvertDiscordToIRC(string message)
        {
            if (!config.formatting.convertFormatting)
                return message;

            // Bold: **text** -> IRC bold
            message = Regex.Replace(message, @"\*\*(.*?)\*\*", match =>
            {
                return $"{IRC_BOLD}{match.Groups[1].Value}{IRC_BOLD}";
            });

            // Underline: __text__ -> IRC underline
            message = Regex.Replace(message, @"__(.*?)__", match =>
            {
                return $"{IRC_UNDERLINE}{match.Groups[1].Value}{IRC_UNDERLINE}";
            });

            // Italic: *text* or _text_ -> IRC italic
            message = Regex.Replace(message, @"(?<!\*)\*([^*\n]+?)\*(?!\*)|(?<!_)_([^_\n]+?)_(?!_)", match =>
            {
                string text = match.Groups[1].Value + match.Groups[2].Value;
                return $"{IRC_ITALIC}{text}{IRC_ITALIC}";
            });

            // Strikethrough: ~~text~~ -> IRC strikethrough
            message = Regex.Replace(message, @"~~(.*?)~~", match =>
            {
                return $"{IRC_STRIKETHROUGH}{match.Groups[1].Value}{IRC_STRIKETHROUGH}";
            });

            // Code blocks: ```text``` -> remove formatting (IRC doesn't have monospace)
            message = Regex.Replace(message, @"```[\s\S]*?```", match =>
            {
                return match.Value.Replace("```", "");
            });

            // Inline code: `text` -> remove backticks
            message = Regex.Replace(message, @"`([^`]+)`", "$1");

            return message;
        }

        // Convert IRC formatting codes to Discord markdown
        private static string ConvertIRCToDiscord(string message)
        {
            if (!config.formatting.convertFormatting)
                return message;

            // Remove color codes first (they don't translate well to Discord)
            message = Regex.Replace(message, $@"{IRC_COLOR}(\d{{1,2}}(,\d{{1,2}})?)?", "");

            // Bold
            message = Regex.Replace(message, $@"{IRC_BOLD}([^{IRC_BOLD}]*?){IRC_BOLD}", "**$1**");
            
            // Handle unclosed bold (toggle behavior)
            var boldParts = message.Split(IRC_BOLD);
            if (boldParts.Length > 1)
            {
                var result = boldParts[0];
                bool inBold = false;
                for (int i = 1; i < boldParts.Length; i++)
                {
                    if (inBold)
                    {
                        result += "**" + boldParts[i];
                        inBold = false;
                    }
                    else
                    {
                        result += "**" + boldParts[i];
                        inBold = true;
                    }
                }
                if (inBold)
                    result += "**";
                message = result;
            }

            // Italic (standard \x1D)
            message = Regex.Replace(message, $@"{IRC_ITALIC}([^{IRC_ITALIC}]*?){IRC_ITALIC}", "*$1*");
            
            // Handle unclosed italic (toggle behavior)
            var italicParts = message.Split(IRC_ITALIC);
            if (italicParts.Length > 1)
            {
                var result = italicParts[0];
                bool inItalic = false;
                for (int i = 1; i < italicParts.Length; i++)
                {
                    if (inItalic)
                    {
                        result += "*" + italicParts[i];
                        inItalic = false;
                    }
                    else
                    {
                        result += "*" + italicParts[i];
                        inItalic = true;
                    }
                }
                if (inItalic)
                    result += "*";
                message = result;
            }

            // Italic (PIRCH98 \x16)
            message = Regex.Replace(message, $@"{IRC_ITALIC_PIRCH98}([^{IRC_ITALIC_PIRCH98}]*?){IRC_ITALIC_PIRCH98}", "*$1*");
            
            // Handle unclosed PIRCH98 italic (toggle behavior)
            var pirchItalicParts = message.Split(IRC_ITALIC_PIRCH98);
            if (pirchItalicParts.Length > 1)
            {
                var result = pirchItalicParts[0];
                bool inItalic = false;
                for (int i = 1; i < pirchItalicParts.Length; i++)
                {
                    if (inItalic)
                    {
                        result += "*" + pirchItalicParts[i];
                        inItalic = false;
                    }
                    else
                    {
                        result += "*" + pirchItalicParts[i];
                        inItalic = true;
                    }
                }
                if (inItalic)
                    result += "*";
                message = result;
            }

            // Underline
            message = Regex.Replace(message, $@"{IRC_UNDERLINE}([^{IRC_UNDERLINE}]*?){IRC_UNDERLINE}", "__$1__");
            
            // Handle unclosed underline (toggle behavior)
            var underlineParts = message.Split(IRC_UNDERLINE);
            if (underlineParts.Length > 1)
            {
                var result = underlineParts[0];
                bool inUnderline = false;
                for (int i = 1; i < underlineParts.Length; i++)
                {
                    if (inUnderline)
                    {
                        result += "__" + underlineParts[i];
                        inUnderline = false;
                    }
                    else
                    {
                        result += "__" + underlineParts[i];
                        inUnderline = true;
                    }
                }
                if (inUnderline)
                    result += "__";
                message = result;
            }

            // Strikethrough
            message = Regex.Replace(message, $@"{IRC_STRIKETHROUGH}([^{IRC_STRIKETHROUGH}]*?){IRC_STRIKETHROUGH}", "~~$1~~");
            
            // Handle unclosed strikethrough (toggle behavior)
            var strikeParts = message.Split(IRC_STRIKETHROUGH);
            if (strikeParts.Length > 1)
            {
                var result = strikeParts[0];
                bool inStrike = false;
                for (int i = 1; i < strikeParts.Length; i++)
                {
                    if (inStrike)
                    {
                        result += "~~" + strikeParts[i];
                        inStrike = false;
                    }
                    else
                    {
                        result += "~~" + strikeParts[i];
                        inStrike = true;
                    }
                }
                if (inStrike)
                    result += "~~";
                message = result;
            }

            // Remove reset characters
            message = message.Replace(IRC_RESET.ToString(), "");

            return message;
        }

        // Helper method to safely make HTTP requests
        private static async Task<(bool success, HttpResponseMessage response)> SafeHttpGetAsync(string url)
        {
            try
            {
                var response = await client.GetAsync(url);
                return (true, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HTTP request failed for {url}: {ex.Message}");
                return (false, null);
            }
        }

        private Task OnDiscordMessage(SocketMessage message)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessDiscordMessage(message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing Discord message: {ex.Message}");
                }
            });
            
            return Task.CompletedTask;
        }

        private async Task ProcessDiscordMessage(SocketMessage message)
        {
            // Skip if it's our own bot user:
            if (message.Author.Id == discord.CurrentUser.Id)
                return;

            // If it's a webhook message, skip only if it's from OUR webhook
            if (message.Author.IsWebhook)
            {
                if (discordWebhookIDs.ContainsValue(message.Author.Id))
                    return;
            }

            // If the message was sent in a channel we don't care about
            if (!config.channels.ContainsValue(message.Channel.Id))
                return;

            // Grab the actual channel as SocketTextChannel
            var discordChannel = message.Channel as SocketTextChannel;

            // Resolve username
            string username = message.Author.Username;

            // Check if this is a reply to another message
            string replyString = "";
            if (message.Reference != null && message.Reference.MessageId.IsSpecified)
            {
                var refMsg = discordChannel.GetMessageAsync(message.Reference.MessageId.Value).Result;
                if (refMsg != null)
                {
                    var refUser = refMsg.Author as SocketGuildUser;
                    var refName = refMsg.Author.Username;
                    replyString = $", replying to {refName}";
                }
            }

            // Replace user mention IDs with nicknames/usernames
            string replacedContent = message.Content;
            foreach (var mentionedUser in message.MentionedUsers)
            {
                var mentionName = (mentionedUser as SocketGuildUser)?.Nickname ?? mentionedUser.Username;
                replacedContent = replacedContent.Replace($"<@{mentionedUser.Id}>", mentionName);
                replacedContent = replacedContent.Replace($"<@!{mentionedUser.Id}>", mentionName);
            }

            //Simplify emojis for IRC
            replacedContent = Regex.Replace(replacedContent, "<a?:(?<name>[^:]+):\\d+>", ":${name}:");

            //Replace stupid shit
            replacedContent = replacedContent.Replace("’","'");
            replacedContent = replacedContent.Replace("“","\"");
            replacedContent = replacedContent.Replace("”","\"");
            replacedContent = replacedContent.Replace("‘","'");
            replacedContent = replacedContent.Replace("’","'");

            // Convert Discord markdown to IRC formatting
            replacedContent = ConvertDiscordToIRC(replacedContent);

            // Find the corresponding IRC channel
            string ircChannel = config.channels.FirstOrDefault(x => x.Value == message.Channel.Id).Key;
            if (ircChannel == null)
                return;

            // Send each line of the message to IRC with the reply indicator (if any)
            if (replacedContent.Contains('\n'))
            {
                foreach (string line in replacedContent.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        irc.SendMessage(SendType.Message, ircChannel, $"<{username}{replyString}> {line}");
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(replacedContent))
                    irc.SendMessage(SendType.Message, ircChannel, $"<{username}{replyString}> {replacedContent}");
            }

            // Handle attachments
            if (message.Attachments != null)
            {
                foreach (var attachment in message.Attachments)
                {
                    irc.SendMessage(SendType.Message, ircChannel, $"<{username}{replyString}> {attachment.Url}");
                    
                    var (success, resp) = await SafeHttpGetAsync(attachment.Url);
                    if (success && resp.StatusCode == HttpStatusCode.OK)
                    {
                        irc.SendMessage(SendType.Message, ircChannel,
                            $"^ [{resp.Content.Headers.ContentType}] ({resp.Content.Headers.ContentLength / 1024.0f} KB)");
                    }
                    else if (!success)
                    {
                        irc.SendMessage(SendType.Message, ircChannel,
                            $"^ [Error fetching attachment info]");
                    }
                }
            }

            // Search for links in the message
            foreach (string word in replacedContent.Split(' '))
            {
                if (!Uri.IsWellFormedUriString(word, UriKind.Absolute))
                    continue;

                var uri = new Uri(word);
                if (!uri.IsWellFormedOriginalString() ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    continue;

                var (success, msg) = await SafeHttpGetAsync(word);
                if (!success || msg.StatusCode != HttpStatusCode.OK)
                    continue;

                if (!msg.Content.Headers.ContentType.ToString().Contains("text/html"))
                {
                    irc.SendMessage(SendType.Message, ircChannel,
                        $"^ [{msg.Content.Headers.ContentType}] ({msg.Content.Headers.ContentLength / 1024.0f} KB)");
                }
                else
                {
                    try
                    {
                        string content = await msg.Content.ReadAsStringAsync();
                        string title = Regex.Match(content,
                            @"\<title\b[^>]*\>\s*(?<Title>[\s\S]*?)\</title\>", RegexOptions.IgnoreCase).Groups["Title"].Value;
                        if (!string.IsNullOrWhiteSpace(title))
                            irc.SendMessage(SendType.Message, ircChannel, $"^ {title}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error reading HTML content: {ex.Message}");
                    }
                }
            }
        }

        private Task OnMessageUpdated(Cacheable<IMessage, ulong> cacheable, SocketMessage message, ISocketMessageChannel ch)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessMessageUpdated(cacheable, message, ch);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing message update: {ex.Message}");
                }
            });
            
            return Task.CompletedTask;
        }

        private async Task ProcessMessageUpdated(Cacheable<IMessage, ulong> cacheable, SocketMessage message, ISocketMessageChannel ch)
        {
            // If the message was from a bot, ignore
            if (message.Author.IsBot)
                return;

            // If the message was in a channel we don't care about, ignore
            if (!config.channels.ContainsValue(message.Channel.Id))
                return;
                
            // Ignore automatic edits that happen shortly after posting (usually for embedding images/GIFs)
            if (DateTime.Now.Subtract(message.Timestamp.DateTime).TotalSeconds < 3 || DateTime.Now.Subtract(message.Timestamp.DateTime).TotalSeconds > 180)
                return;

            // Resolve nickname or username
            var user = (message.Author as SocketGuildUser);
            string username = message.Author.Username;

            // Check if this is a reply
            string replyString = "";
            var discordChannel = message.Channel as SocketTextChannel;
            if (message.Reference != null && message.Reference.MessageId.IsSpecified)
            {
                var refMsg = discordChannel.GetMessageAsync(message.Reference.MessageId.Value).Result;
                if (refMsg != null)
                {
                    var refUser = refMsg.Author as SocketGuildUser;
                    var refName = refMsg.Author.Username;
                    replyString = $", replying to {refName}";
                }
            }

            // Replace mentions
            string replacedContent = message.Content;
            foreach (var mentionedUser in message.MentionedUsers)
            {
                var mentionName = (mentionedUser as SocketGuildUser)?.Nickname ?? mentionedUser.Username;
                replacedContent = replacedContent.Replace($"<@{mentionedUser.Id}>", mentionName);
                replacedContent = replacedContent.Replace($"<@!{mentionedUser.Id}>", mentionName);
            }

            //Simplify emojis for IRC
            replacedContent = Regex.Replace(replacedContent, "<a?:(?<name>[^:]+):\\d+>", ":${name}:");

            // Convert Discord markdown to IRC formatting
            replacedContent = ConvertDiscordToIRC(replacedContent);

            // Find the IRC channel name
            string ircChannel = config.channels.FirstOrDefault(x => x.Value == message.Channel.Id).Key;
            if (ircChannel == null)
                return;

            // Send each line as an EDIT
            if (!string.IsNullOrWhiteSpace(replacedContent))
            {
                if (replacedContent.Contains('\n'))
                {
                    foreach (string line in replacedContent.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            irc.SendMessage(SendType.Message, ircChannel, $"<EDIT> <{username}{replyString}> {line}");
                    }
                }
                else
                {
                    irc.SendMessage(SendType.Message, ircChannel, $"<EDIT> <{username}{replyString}> {replacedContent}");
                }
            }

            // The rest of the link title/metadata logic
            foreach (string word in replacedContent.Split(' '))
            {
                if (!Uri.IsWellFormedUriString(word, UriKind.Absolute))
                    continue;

                var (success, msg) = await SafeHttpGetAsync(word);
                if (!success || msg.StatusCode != HttpStatusCode.OK)
                    continue;

                if (!msg.Content.Headers.ContentType.ToString().Contains("text/html"))
                {
                    irc.SendMessage(SendType.Message, ircChannel,
                        $"^ [{msg.Content.Headers.ContentType}] ({msg.Content.Headers.ContentLength / 1024.0f} KB)");
                }
                else
                {
                    try
                    {
                        string content = await msg.Content.ReadAsStringAsync();
                        string title = Regex.Match(content,
                            @"\<title\b[^>]*\>\s*(?<Title>[\s\S]*?)\</title\>", RegexOptions.IgnoreCase).Groups["Title"].Value;
                        if (!string.IsNullOrWhiteSpace(title))
                            irc.SendMessage(SendType.Message, ircChannel, $"^ {title}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error reading HTML content: {ex.Message}");
                    }
                }
            }
        }

        private static void OnIRCMessage(object sender, IrcEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessIRCMessage(sender, e);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing IRC message: {ex.Message}");
                }
            });
        }

        private static async Task ProcessIRCMessage(object sender, IrcEventArgs e)
        {
            // First, handle emotes in the message
            string messageContent = WithEmotes(e.Data.Message);

            // Convert IRC formatting to Discord markdown
            messageContent = ConvertIRCToDiscord(messageContent);

            // Now, let's process the message to convert user nicknames to mentions
            // Only do this if the feature is enabled in the config
            if (config.formatting.ircMentionsDiscord)
            {
                // This pattern will match words starting with @ but will avoid @everyone and @here
                var words = messageContent.Split(' ');
                for (int i = 0; i < words.Length; i++)
                {
                    string word = words[i];
                    
                    // Check if this is a mention (starts with @)
                    if (word.StartsWith("@") && word.Length > 1)
                    {
                        string nickname = word.Substring(1); // Remove the @ symbol
                        
                        // Skip reserved mentions to avoid pinging everyone
                        if (nickname.Equals("everyone", StringComparison.OrdinalIgnoreCase) || 
                            nickname.Equals("here", StringComparison.OrdinalIgnoreCase))
                        {
                            // Replace with a safe version that won't ping
                            words[i] = word.Replace("@", "@\u200B"); // Add a zero-width space after @
                            continue;
                        }
                        
                        // Try to find the user with this nickname or username
                        SocketUser user = discord.GetGuild(config.discordServerId).Users
                            .FirstOrDefault(x => 
                                (x.Nickname != null && x.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase)) || 
                                x.Username.Equals(nickname, StringComparison.OrdinalIgnoreCase) ||
                                x.GlobalName != null && x.GlobalName.Equals(nickname, StringComparison.OrdinalIgnoreCase));
                        
                        if (user != null)
                        {
                            // Replace the word with a proper Discord mention
                            words[i] = user.Mention;
                        }
                    }
                    // Also handle the case where nickname: is used at the beginning (the original functionality)
                    else if (i == 0 && word.EndsWith(':'))
                    {
                        string potentialNickname = word.Substring(0, word.Length - 1);
                        
                        SocketUser user = discord.GetGuild(config.discordServerId).Users
                            .FirstOrDefault(x => 
                                (x.Nickname != null && x.Nickname.Equals(potentialNickname, StringComparison.OrdinalIgnoreCase)) || 
                                x.Username.Equals(potentialNickname, StringComparison.OrdinalIgnoreCase) ||
                                x.GlobalName != null && x.GlobalName.Equals(potentialNickname, StringComparison.OrdinalIgnoreCase));
                        
                        if (user != null)
                        {
                            // Replace with mention
                            words[i] = user.Mention + ":";
                        }
                    }
                }
                
                // Rebuild the message with the mentions replaced
                messageContent = string.Join(" ", words);
            }

            // Search for links in the IRC message
            foreach (string word in messageContent.Split(' '))
            {
                if (!Uri.IsWellFormedUriString(word, UriKind.Absolute))
                    break;

                Uri uri = new Uri(word);
                if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    break;

                var (success, msg) = await SafeHttpGetAsync(word);
                if (!success || msg.StatusCode != HttpStatusCode.OK)
                    continue;

                if (!msg.Content.Headers.ContentType.ToString().Contains("text/html"))
                {
                    irc.SendMessage(SendType.Message, e.Data.Channel,
                        $"^ [{msg.Content.Headers.ContentType}] ({msg.Content.Headers.ContentLength / 1024.0f} KB)");
                }
                else
                {
                    try
                    {
                        string content = await msg.Content.ReadAsStringAsync();
                        string title = Regex.Match(content,
                            @"\<title\b[^>]*\>\s*(?<Title>[\s\S]*?)\</title\>", RegexOptions.IgnoreCase).Groups["Title"].Value;
                        if (!string.IsNullOrWhiteSpace(title))
                            irc.SendMessage(SendType.Message, e.Data.Channel, $"^ {title}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error reading HTML content: {ex.Message}");
                    }
                }
            }

            // Send to Discord
            if (config.formatting.useWebhooks)
            {
                // Attempt to find a matching user for a nice avatar
                string avatarUrl = null;
                SocketGuildUser matchingUser = discord
                    .GetGuild(config.discordServerId)
                    .Users
                    .FirstOrDefault(x =>
                        string.Equals(x.GlobalName, e.Data.Nick, StringComparison.OrdinalIgnoreCase)
                        || (x.Nickname != null && x.Nickname.Equals(e.Data.Nick, StringComparison.OrdinalIgnoreCase))
                        || x.Username.Equals(e.Data.Nick, StringComparison.OrdinalIgnoreCase));

                string possibleNickname = matchingUser?.Nickname;
                if (matchingUser != null)
                    avatarUrl = matchingUser.GetAvatarUrl();

                // Use the IRC channel name to get the correct webhook
                string channelKey = e.Data.Channel.ToLower();
                if (discordWebhooks.ContainsKey(channelKey))
                {
                    try
                    {
                        await discordWebhooks[channelKey].SendMessageAsync(
                            messageContent,
                            username: possibleNickname ?? e.Data.Nick,
                            avatarUrl: avatarUrl
                        );
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error sending webhook message: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"No webhook found for IRC channel {e.Data.Channel}");
                }
            }
            else
            {
                // Fallback to sending as a normal message
                if (!discordChannels.ContainsKey(e.Data.Channel))
                {
                    Console.WriteLine($"Could not get Discord channel for #{e.Data.Channel} on IRC");
                    return;
                }
                var discordChannel = discordChannels[e.Data.Channel];
                try
                {
                    await discordChannel.SendMessageAsync($"{config.formatting.discordPrefix.Replace("%u", e.Data.Nick)} {messageContent}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending Discord message: {ex.Message}");
                }
            }
        }

        private static string WithEmotes(string message)
        {
            string[] words = message.Split(' ');
            foreach (string word in words)
            {
                // Check if it looks like :emoteName:
                if (word.StartsWith(":") && word.EndsWith(":"))
                {
                    Emote emote = GetEmoteFromName(word.Replace(":", ""));
                    if (emote != null)
                        message = message.Replace(word, $"{emote}");
                }
            }
            return message;
        }

        private static Emote GetEmoteFromName(string name)
        {
            return discord.GetGuild(config.discordServerId)
                          .GetEmotesAsync()
                          .Result
                          .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    class RelayConfig
    {
        // Discord stuff
        public string discordToken { get; set; } = "TOKEN";
        public ulong discordServerId { get; set; } = 0;

        // IRC stuff
        public string IRCNickname { get; set; } = "Discord-IRC";
        public string IRCIp { get; set; } = "irc.example.com";
        public int IRCport { get; set; } = 6667;
        public bool useNickServ { get; set; } = false;
        public string nickServPass { get; set; } = "NONE";
        public Dictionary<string, ulong> channels { get; set; } = new Dictionary<string, ulong>();

        // Formatting
        public FormattingConfig formatting { get; set; } = new FormattingConfig();
    }

    class FormattingConfig
    {
        public string discordPrefix { get; set; } = "**<%u/IRC>**";
        public bool ircMentionsDiscord { get; set; } = true;  // Defaults to true
        public bool useWebhooks { get; set; } = false;
        public bool convertFormatting { get; set; } = true;  // Convert formatting, defaults to true
    }
}