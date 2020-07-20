using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DarkBot;
using Discord;
using Discord.WebSocket;

namespace DarkBotBackup
{
    public class Backup : BotModule
    {
        private DiscordSocketClient _client;
        //Channel ID, MessageID
        private ConcurrentDictionary<ulong, ulong> channelRead = new ConcurrentDictionary<ulong, ulong>();
        private ConcurrentDictionary<ulong, string> channelNames = new ConcurrentDictionary<ulong, string>();
        private ConcurrentQueue<AttachmentDownload> attachmentDownloads = new ConcurrentQueue<AttachmentDownload>();
        string channelReadPath = Path.Combine(Environment.CurrentDirectory, "Backup", "ChannelRead.txt");
        string channelNamesPath = Path.Combine(Environment.CurrentDirectory, "Backup", "ChannelNames.txt");

        public Task Initialize(IServiceProvider services)
        {
            _client = (DiscordSocketClient)services.GetService(typeof(DiscordSocketClient));
            _client.Ready += ReadyAsync;
            _client.MessageReceived += MessageReceivedAsync;
            LoadChannelRead();
            return Task.CompletedTask;
        }

        public async void BackupServer()
        {
            foreach (SocketGuild server in _client.Guilds)
            {
                foreach (SocketGuildChannel channel in server.Channels)
                {
                    var textChannel = channel as SocketTextChannel;
                    if (textChannel != null)
                    {
                        SetChannelName(textChannel.Id, textChannel.Name);
                        await BackupChannel(textChannel);
                    }
                }
            }
        }

        public async void DownloadEmbeds()
        {
            string backupPath = Path.Combine(Environment.CurrentDirectory, "Backup");
            using (HttpClient hc = new HttpClient())
            {
                while (true)
                {
                    if (attachmentDownloads.TryDequeue(out AttachmentDownload ad))
                    {
                        try
                        {
                            string outputPath = Path.Combine(backupPath, ad.channelID.ToString());
                            Directory.CreateDirectory(outputPath);
                            HttpResponseMessage fileDownload = await hc.GetAsync(ad.url);
                            if (fileDownload.IsSuccessStatusCode)
                            {
                                string fileName = Path.Combine(outputPath, ad.messageID + "-" + ad.attachmentID);
                                string contentType = fileDownload.Content.Headers.ContentType.MediaType;
                                switch (contentType)
                                {
                                    case "image/bmp":
                                        fileName += ".bmp";
                                        break;
                                    case "image/gif":
                                        fileName += ".gif";
                                        break;
                                    case "image/jpeg":
                                        fileName += ".jpg";
                                        break;
                                    case "image/png":
                                        fileName += ".png";
                                        break;
                                    case "image/tiff":
                                        fileName += ".tiff";
                                        break;
                                    case "image/webp":
                                        fileName += ".webp";
                                        break;
                                    case "video/mp4":
                                        fileName += ".mp4";
                                        break;
                                    case "video/ogg":
                                        fileName += ".ogg";
                                        break;
                                    case "video/webm":
                                        fileName += ".webm";
                                        break;
                                    case "text/plain":
                                        fileName += ".txt";
                                        break;
                                    default:
                                        Console.WriteLine("Unknown content type: " + contentType);
                                        fileName += ".bin";
                                        break;
                                }
                                using (FileStream fs = new FileStream(fileName, FileMode.Create))
                                {
                                    await fileDownload.Content.CopyToAsync(fs);
                                    Console.WriteLine($"Downloaded {ad.url} for message {ad.messageID}");
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"Error downloading {ad.url} for message {ad.messageID}, error: {e.Message}");
                        }
                    }
                    await Task.Delay(1000);
                }
            }
        }

        private async Task BackupChannel(SocketTextChannel channel)
        {
            Console.WriteLine($"Backing up {channel.Name} on server {channel.Guild.Name}");
            ulong fromMessage = GetChannelRead(channel.Id);
            while (true)
            {
                IAsyncEnumerable<IReadOnlyCollection<IMessage>> asyncMessages = channel.GetMessagesAsync(fromMessage, Direction.After);
                bool newMessage = false;
                await foreach (IReadOnlyCollection<IMessage> messages in asyncMessages)
                {
                    foreach (IMessage message in messages)
                    {
                        //Discord gives us the list in reverse order so we want to take just the first one as our "read" point
                        if (newMessage == false)
                        {
                            fromMessage = message.Id;
                        }
                        newMessage = true;
                        BackupMessage(channel, message);
                    }
                }
                if (!newMessage)
                {
                    break;
                }
                SetChannelRead(channel.Id, fromMessage);
                await Task.Delay(1000);
                while (attachmentDownloads.Count > 0)
                {
                    await Task.Delay(1000);
                }
            }
            Console.WriteLine($"Done backing up {channel.Name} on server {channel.Guild.Name}");
        }

        private void BackupMessage(SocketTextChannel channel, IMessage message)
        {
            foreach (IAttachment attachment in message.Attachments)
            {
                Console.WriteLine($"Queued download for message: {message.Id}");
                attachmentDownloads.Enqueue(new AttachmentDownload(channel.Guild.Id, channel.Id, message.Id, attachment.Id, attachment.Url));
            }
        }


        private Task ReadyAsync()
        {
            Console.WriteLine("Starting backup service");
            BackupServer();
            DownloadEmbeds();
            return Task.CompletedTask;
        }

        private Task MessageReceivedAsync(SocketMessage message)
        {
            SocketTextChannel textChannel = message.Channel as SocketTextChannel;
            if (textChannel != null)
            {
                SetChannelName(textChannel.Id, textChannel.Name);
                BackupMessage(textChannel, message);
                channelRead[textChannel.Id] = message.Id;
            }

            return Task.CompletedTask;
        }

        private void SetChannelRead(ulong channelID, ulong messageID)
        {
            channelRead[channelID] = messageID;
            SaveChannelRead();
        }

        private ulong GetChannelRead(ulong channelID)
        {
            if (channelRead.TryGetValue(channelID, out ulong messageID))
            {
                return messageID;
            }
            return 0;
        }

        private void LoadChannelRead()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(channelReadPath));
            if (File.Exists(channelReadPath))
            {
                using (StreamReader sr = new StreamReader(channelReadPath))
                {
                    channelRead.Clear();
                    string currentLine = null;
                    while ((currentLine = sr.ReadLine()) != null)
                    {
                        currentLine = currentLine.Trim();
                        string lhs = currentLine.Substring(0, currentLine.IndexOf("="));
                        string rhs = currentLine.Substring(currentLine.IndexOf("=") + 1);
                        if (ulong.TryParse(lhs, out ulong channelID) && ulong.TryParse(rhs, out ulong messageID))
                        {
                            channelRead[channelID] = messageID;
                        }
                    }
                }
                Console.WriteLine("Loaded channel read positions");
            }
            else
            {
                Console.WriteLine("First run - no channel read positions to load");
            }
        }

        private void SaveChannelRead()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(channelReadPath));
            if (File.Exists(channelReadPath))
            {
                File.Delete(channelReadPath);
            }
            using (StreamWriter sw = new StreamWriter(channelReadPath))
            {
                foreach (KeyValuePair<ulong, ulong> kvp in channelRead)
                {
                    sw.WriteLine($"{kvp.Key}={kvp.Value}");
                }
            }
        }

        private void LoadChannelNames()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(channelNamesPath));
            if (File.Exists(channelNamesPath))
            {
                using (StreamReader sr = new StreamReader(channelNamesPath))
                {
                    channelRead.Clear();
                    string currentLine = null;
                    while ((currentLine = sr.ReadLine()) != null)
                    {
                        currentLine = currentLine.Trim();
                        string lhs = currentLine.Substring(0, currentLine.IndexOf("="));
                        string rhs = currentLine.Substring(currentLine.IndexOf("=") + 1);
                        if (ulong.TryParse(lhs, out ulong channelID))
                        {
                            channelNames[channelID] = rhs;
                        }
                    }
                }
                Console.WriteLine("Loaded channel names");
            }
            else
            {
                Console.WriteLine("First run - no channel names to load");
            }
        }

        private void SaveChannelNames()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(channelNamesPath));
            if (File.Exists(channelNamesPath))
            {
                File.Delete(channelNamesPath);
            }
            using (StreamWriter sw = new StreamWriter(channelNamesPath))
            {
                foreach (KeyValuePair<ulong, string> kvp in channelNames)
                {
                    sw.WriteLine($"{kvp.Key}={kvp.Value}");
                }
            }
        }

        private void SetChannelName(ulong channelID, string channelName)
        {
            if (channelNames.TryGetValue(channelID, out string oldname))
            {
                if (oldname == channelName)
                {
                    return;
                }
            }
            channelNames[channelID] = channelName;
            SaveChannelNames();
        }
    }
}
