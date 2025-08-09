using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using Newtonsoft.Json;
using WTelegram;
using TL;
using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace TelegramEAManager
{
    public class EnhancedTelegramService : IDisposable
    {
        private Client? client;
        private int apiId;
        private string apiHash = "";
        private string phoneNumber = "";
        private User? me;

        // Enhanced message tracking with timestamps and deduplication
        private readonly ConcurrentDictionary<long, ChannelMonitoringState> channelStates = new();
        private readonly ConcurrentDictionary<string, DateTime> processedMessageHashes = new();
        private readonly ConcurrentQueue<PendingMessage> messageProcessingQueue = new();

        // Monitoring infrastructure
        private System.Threading.Timer? messagePollingTimer;
        private System.Threading.Timer? healthCheckTimer;
        private System.Threading.Timer? cleanupTimer;
        private readonly List<long> monitoredChannels = new();
        private volatile bool isMonitoring = false;

        // Performance metrics
        private readonly PerformanceMetrics metrics = new();
        private readonly SemaphoreSlim pollingLock = new(1, 1);
        private readonly SemaphoreSlim processingLock = new(1, 1);

        // Connection resilience
        private int reconnectAttempts = 0;
        private readonly int maxReconnectAttempts = 5;
        private readonly TimeSpan[] reconnectDelays = {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };

        // Events
        public event EventHandler<SignalEventArgs>? NewSignalReceived;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<string>? DebugMessage;
        public event EventHandler<MonitoringStatusEventArgs>? MonitoringStatusChanged;
        public event EventHandler<ChannelHealthEventArgs>? ChannelHealthUpdated;

        public EnhancedTelegramService()
        {
            LoadApiCredentials();
            StartBackgroundServices();
        }

        #region API Credentials Management

        private void LoadApiCredentials()
        {
            if (TryLoadFromSettingsFile())
                return;

            if (TryLoadFromAppConfig())
                return;

            ShowApiSetupDialog();
        }

        private bool TryLoadFromSettingsFile()
        {
            try
            {
                string settingsFile = "telegram_api.json";
                if (File.Exists(settingsFile))
                {
                    var json = File.ReadAllText(settingsFile);
                    var settings = JsonConvert.DeserializeObject<ApiSettings>(json);

                    if (settings != null && settings.ApiId > 0 && !string.IsNullOrEmpty(settings.ApiHash))
                    {
                        apiId = settings.ApiId;
                        apiHash = settings.ApiHash;
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore errors, try next method
            }
            return false;
        }

        private bool TryLoadFromAppConfig()
        {
            try
            {
                var apiIdStr = ConfigurationManager.AppSettings["TelegramApiId"];
                var apiHashStr = ConfigurationManager.AppSettings["TelegramApiHash"];

                if (!string.IsNullOrEmpty(apiIdStr) && !string.IsNullOrEmpty(apiHashStr))
                {
                    apiId = int.Parse(apiIdStr);
                    apiHash = apiHashStr;
                    return true;
                }
            }
            catch
            {
                // Ignore errors, show setup dialog
            }
            return false;
        }

        private void ShowApiSetupDialog()
        {
            using (var setupForm = new Form())
            {
                setupForm.Text = "🔑 Telegram API Setup - islamahmed9717";
                setupForm.Size = new Size(600, 500);
                setupForm.StartPosition = FormStartPosition.CenterScreen;
                setupForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                setupForm.MaximizeBox = false;
                setupForm.MinimizeBox = false;
                setupForm.BackColor = Color.White;

                var lblTitle = new Label
                {
                    Text = "🔑 TELEGRAM API CREDENTIALS SETUP",
                    Location = new Point(20, 20),
                    Size = new Size(550, 30),
                    Font = new Font("Segoe UI", 14F, FontStyle.Bold),
                    ForeColor = Color.FromArgb(37, 99, 235),
                    TextAlign = ContentAlignment.MiddleCenter
                };
                setupForm.Controls.Add(lblTitle);

                var lblInstructions = new Label
                {
                    Text = "📋 FOLLOW THESE STEPS TO GET YOUR API CREDENTIALS:",
                    Location = new Point(20, 60),
                    Size = new Size(550, 25),
                    Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                    ForeColor = Color.FromArgb(249, 115, 22)
                };
                setupForm.Controls.Add(lblInstructions);

                var instructions = new TextBox
                {
                    Location = new Point(20, 90),
                    Size = new Size(550, 180),
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    Font = new Font("Segoe UI", 10F),
                    Text = @"STEP 1: 🌐 Open your web browser and go to: https://my.telegram.org

STEP 2: 📱 Login with your phone number (same as you'll use in this app)

STEP 3: 🔐 Enter the verification code sent to your Telegram app

STEP 4: 🆕 Click ""API development tools""

STEP 5: 📝 Fill out the form:
   • App title: Telegram EA Manager
   • Short name: ea_manager_" + DateTime.Now.ToString("yyyyMMdd") + @"
   • Description: Trading signal manager for islamahmed9717
   • Platform: Desktop
   • URL: (leave empty)

STEP 6: ✅ Click ""Create application""

STEP 7: 📋 Copy the api_id (numbers) and api_hash (long string) below"
                };
                setupForm.Controls.Add(instructions);

                var lblApiId = new Label
                {
                    Text = "📋 API ID (numbers only):",
                    Location = new Point(20, 290),
                    Size = new Size(200, 25),
                    Font = new Font("Segoe UI", 10F, FontStyle.Bold)
                };
                setupForm.Controls.Add(lblApiId);

                var txtApiId = new TextBox
                {
                    Location = new Point(230, 290),
                    Size = new Size(200, 25),
                    Font = new Font("Segoe UI", 11F),
                    PlaceholderText = "e.g. 1234567"
                };
                txtApiId.KeyPress += (s, e) =>
                {
                    if (!char.IsDigit(e.KeyChar) && e.KeyChar != 8)
                        e.Handled = true;
                };
                setupForm.Controls.Add(txtApiId);

                var lblApiHash = new Label
                {
                    Text = "🔑 API Hash (long string):",
                    Location = new Point(20, 330),
                    Size = new Size(200, 25),
                    Font = new Font("Segoe UI", 10F, FontStyle.Bold)
                };
                setupForm.Controls.Add(lblApiHash);

                var txtApiHash = new TextBox
                {
                    Location = new Point(230, 330),
                    Size = new Size(340, 25),
                    Font = new Font("Segoe UI", 11F),
                    PlaceholderText = "e.g. abcd1234efgh5678..."
                };
                setupForm.Controls.Add(txtApiHash);

                var btnOpenWebsite = new Button
                {
                    Text = "🌐 OPEN TELEGRAM API WEBSITE",
                    Location = new Point(450, 290),
                    Size = new Size(120, 65),
                    BackColor = Color.FromArgb(34, 197, 94),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 9F, FontStyle.Bold)
                };
                btnOpenWebsite.Click += (s, e) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://my.telegram.org") { UseShellExecute = true });
                    }
                    catch
                    {
                        MessageBox.Show("Please manually open: https://my.telegram.org", "Open Browser", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                };
                setupForm.Controls.Add(btnOpenWebsite);

                var btnSave = new Button
                {
                    Text = "💾 SAVE & CONTINUE",
                    Location = new Point(200, 380),
                    Size = new Size(150, 40),
                    BackColor = Color.FromArgb(37, 99, 235),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                    DialogResult = DialogResult.OK
                };
                setupForm.Controls.Add(btnSave);

                var btnExit = new Button
                {
                    Text = "❌ EXIT APP",
                    Location = new Point(370, 380),
                    Size = new Size(120, 40),
                    BackColor = Color.FromArgb(220, 38, 38),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                    DialogResult = DialogResult.Cancel
                };
                setupForm.Controls.Add(btnExit);

                btnSave.Click += (s, e) =>
                {
                    if (string.IsNullOrEmpty(txtApiId.Text) || string.IsNullOrEmpty(txtApiHash.Text))
                    {
                        MessageBox.Show("❌ Please enter both API ID and API Hash!", "Missing Information", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    if (!int.TryParse(txtApiId.Text, out int testApiId) || testApiId <= 0)
                    {
                        MessageBox.Show("❌ API ID must be a valid number!", "Invalid API ID", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    if (txtApiHash.Text.Length < 10)
                    {
                        MessageBox.Show("❌ API Hash seems too short. Please check!", "Invalid API Hash", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    apiId = testApiId;
                    apiHash = txtApiHash.Text.Trim();
                    SaveApiCredentials();
                    setupForm.DialogResult = DialogResult.OK;
                };

                setupForm.AcceptButton = btnSave;
                setupForm.CancelButton = btnExit;

                var result = setupForm.ShowDialog();
                if (result != DialogResult.OK)
                {
                    Environment.Exit(0);
                }
            }
        }

        private void SaveApiCredentials()
        {
            try
            {
                var settings = new ApiSettings
                {
                    ApiId = apiId,
                    ApiHash = apiHash,
                    SavedDate = DateTime.UtcNow,
                    Username = "islamahmed9717"
                };

                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText("telegram_api.json", json);

                MessageBox.Show("✅ API credentials saved successfully!\n\n🔐 Your credentials are stored securely in telegram_api.json\n📱 You won't need to enter them again!",
                               "Credentials Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"⚠️ Warning: Could not save credentials to file.\n\n{ex.Message}\n\nYou may need to enter them again next time.",
                               "Save Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        #endregion

        #region Connection Methods

        private string? Config(string what)
        {
            switch (what)
            {
                case "api_id": return apiId.ToString();
                case "api_hash": return apiHash;
                case "phone_number": return phoneNumber;
                case "verification_code": return RequestVerificationCode();
                case "first_name": return "islamahmed9717";
                case "last_name": return "";
                case "password": return RequestPassword();
                case "session_pathname": return "session.dat";
                default: return null;
            }
        }

        private string RequestVerificationCode()
        {
            using (var codeForm = new Form())
            {
                codeForm.Text = "📱 Telegram Verification Code";
                codeForm.Size = new Size(400, 200);
                codeForm.StartPosition = FormStartPosition.CenterScreen;
                codeForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                codeForm.MaximizeBox = false;

                var lblMessage = new Label
                {
                    Text = "📱 Enter the verification code sent to your Telegram:",
                    Location = new Point(20, 20),
                    Size = new Size(350, 40),
                    Font = new Font("Segoe UI", 10F)
                };
                codeForm.Controls.Add(lblMessage);

                var txtCode = new TextBox
                {
                    Location = new Point(20, 70),
                    Size = new Size(200, 25),
                    Font = new Font("Segoe UI", 12F),
                    MaxLength = 6
                };
                codeForm.Controls.Add(txtCode);

                var btnOK = new Button
                {
                    Text = "✅ Confirm",
                    Location = new Point(240, 70),
                    Size = new Size(100, 25),
                    DialogResult = DialogResult.OK
                };
                codeForm.Controls.Add(btnOK);

                codeForm.AcceptButton = btnOK;
                txtCode.Focus();

                if (codeForm.ShowDialog() == DialogResult.OK)
                {
                    return txtCode.Text.Trim();
                }
                return "";
            }
        }

        private string RequestPassword()
        {
            using (var passwordForm = new Form())
            {
                passwordForm.Text = "🔐 Two-Factor Authentication";
                passwordForm.Size = new Size(400, 200);
                passwordForm.StartPosition = FormStartPosition.CenterScreen;
                passwordForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                passwordForm.MaximizeBox = false;

                var lblMessage = new Label
                {
                    Text = "🔐 Enter your 2FA password:",
                    Location = new Point(20, 20),
                    Size = new Size(350, 40),
                    Font = new Font("Segoe UI", 10F)
                };
                passwordForm.Controls.Add(lblMessage);

                var txtPassword = new TextBox
                {
                    Location = new Point(20, 70),
                    Size = new Size(200, 25),
                    Font = new Font("Segoe UI", 12F),
                    UseSystemPasswordChar = true
                };
                passwordForm.Controls.Add(txtPassword);

                var btnOK = new Button
                {
                    Text = "✅ Confirm",
                    Location = new Point(240, 70),
                    Size = new Size(100, 25),
                    DialogResult = DialogResult.OK
                };
                passwordForm.Controls.Add(btnOK);

                passwordForm.AcceptButton = btnOK;
                txtPassword.Focus();

                if (passwordForm.ShowDialog() == DialogResult.OK)
                {
                    return txtPassword.Text;
                }
                return "";
            }
        }

        public async Task<bool> ConnectAsync(string phone)
        {
            try
            {
                phoneNumber = phone;
                client = new Client(Config);
                me = await client.LoginUserIfNeeded();

                if (me != null)
                {
                    reconnectAttempts = 0;
                    OnDebugMessage($"✅ Connected successfully as {me.first_name} {me.last_name}");
                }

                return me != null;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Connection failed: {ex.Message}", "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        public bool IsUserAuthorized()
        {
            return client != null && me != null;
        }

        public async Task<List<ChannelInfo>> GetChannelsAsync()
        {
            var channels = new List<ChannelInfo>();

            try
            {
                if (client == null) return channels;

                var dialogs = await client.Messages_GetAllDialogs();

                foreach (var dialog in dialogs.dialogs)
                {
                    try
                    {
                        if (dialogs.chats.TryGetValue(dialog.Peer.ID, out var chat))
                        {
                            if (chat is Channel channel)
                            {
                                channels.Add(new ChannelInfo
                                {
                                    Id = channel.ID,
                                    Title = channel.Title ?? "",
                                    Username = channel.username ?? "",
                                    Type = DetermineChannelType(channel),
                                    MembersCount = channel.participants_count,
                                    AccessHash = channel.access_hash,
                                    LastActivity = DateTime.UtcNow
                                });

                                OnDebugMessage($"Found channel: {channel.Title} (ID: {channel.ID}, Username: {channel.username})");
                            }
                            else if (chat is Chat regularChat)
                            {
                                if (IsSignalRelatedChat(regularChat.Title))
                                {
                                    channels.Add(new ChannelInfo
                                    {
                                        Id = regularChat.ID,
                                        Title = regularChat.Title ?? "",
                                        Username = "",
                                        Type = "Group",
                                        MembersCount = regularChat.participants_count,
                                        AccessHash = 0,
                                        LastActivity = DateTime.UtcNow
                                    });

                                    OnDebugMessage($"Found chat: {regularChat.Title} (ID: {regularChat.ID})");
                                }
                            }
                        }

                        if (dialog.Peer is PeerUser && dialogs.users.TryGetValue(dialog.Peer.ID, out var user))
                        {
                            if (user.IsBot && IsSignalRelatedChat(user.first_name + " " + user.last_name))
                            {
                                channels.Add(new ChannelInfo
                                {
                                    Id = user.ID,
                                    Title = $"{user.first_name} {user.last_name}".Trim(),
                                    Username = user.username ?? "",
                                    Type = "Bot",
                                    MembersCount = 0,
                                    AccessHash = user.access_hash,
                                    LastActivity = DateTime.UtcNow
                                });

                                OnDebugMessage($"Found bot: {user.first_name} {user.last_name} (ID: {user.ID})");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        OnDebugMessage($"Error processing dialog: {ex.Message}");
                    }
                }

                channels = channels.OrderBy(c => c.Title).ToList();
                OnDebugMessage($"Total channels/chats found: {channels.Count}");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to get channels: {ex.Message}");
            }

            return channels;
        }

        private bool IsSignalRelatedChat(string? title)
        {
            if (string.IsNullOrEmpty(title))
                return false;

            var lowerTitle = title.ToLower();
            var signalKeywords = new[] { "signal", "indicator", "trading", "forex", "crypto", "vip", "premium", "gold", "binary", "options", "scalp", "swing" };

            return signalKeywords.Any(keyword => lowerTitle.Contains(keyword));
        }

        private string DetermineChannelType(Channel channel)
        {
            var title = channel.Title?.ToLower() ?? "";

            if (title.Contains("vip")) return "VIP";
            if (title.Contains("premium")) return "Premium";
            if (title.Contains("signals") || title.Contains("signal")) return "Signals";
            if (title.Contains("gold")) return "Gold";
            if (title.Contains("crypto") || title.Contains("bitcoin") || title.Contains("btc")) return "Crypto";
            if (channel.IsGroup) return "Groups";

            return "Channel";
        }

        #endregion

        #region Background Services

        private void StartBackgroundServices()
        {
            // Message processing worker
            Task.Run(async () => await MessageProcessingWorker());

            // Health monitoring
            healthCheckTimer = new System.Threading.Timer(
                async _ => await PerformHealthCheck(),
                null,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1)
            );

            // Memory cleanup
            cleanupTimer = new System.Threading.Timer(
                _ => PerformCleanup(),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5)
            );
        }

        private async Task MessageProcessingWorker()
        {
            while (!disposedValue)
            {
                try
                {
                    if (messageProcessingQueue.TryDequeue(out var pending))
                    {
                        await ProcessMessageSafely(pending);
                    }
                    else
                    {
                        await Task.Delay(100);
                    }
                }
                catch (Exception ex)
                {
                    OnErrorOccurred($"Message processing worker error: {ex.Message}");
                }
            }
        }

        private async Task PerformHealthCheck()
        {
            if (!isMonitoring || client == null) return;

            try
            {
                var sw = Stopwatch.StartNew();

                // Test connection
                var accountTTL = await client.Account_GetAccountTTL();

                sw.Stop();
                metrics.RecordLatency("HealthCheck", sw.ElapsedMilliseconds);

                // Check individual channel health
                foreach (var kvp in channelStates)
                {
                    var state = kvp.Value;
                    var timeSinceLastMessage = DateTime.UtcNow - state.LastMessageTime;
                    var timeSinceLastPoll = DateTime.UtcNow - state.LastPollTime;

                    // Determine health status
                    var health = DetermineChannelHealth(state, timeSinceLastMessage, timeSinceLastPoll);

                    if (health != state.Health)
                    {
                        state.Health = health;
                        OnChannelHealthUpdated(kvp.Key, state.ChannelName, health);
                    }
                }

                OnDebugMessage($"Health check completed in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Health check failed: {ex.Message}");
                await HandleConnectionFailure();
            }
        }

        private ChannelHealth DetermineChannelHealth(ChannelMonitoringState state, TimeSpan timeSinceLastMessage, TimeSpan timeSinceLastPoll)
        {
            if (state.ConsecutiveErrors > 5)
                return ChannelHealth.Critical;

            if (state.ConsecutiveErrors > 2 || timeSinceLastPoll > TimeSpan.FromMinutes(5))
                return ChannelHealth.Warning;

            if (timeSinceLastMessage > TimeSpan.FromHours(1) && state.MessageCount > 0)
                return ChannelHealth.Inactive;

            return ChannelHealth.Healthy;
        }

        private void PerformCleanup()
        {
            try
            {
                // More aggressive cleanup - keep only last 30 minutes of message hashes
                var cutoffTime = DateTime.UtcNow.AddMinutes(-30);
                var keysToRemove = processedMessageHashes
                    .Where(kvp => kvp.Value < cutoffTime)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    processedMessageHashes.TryRemove(key, out _);
                }

                // Clean metrics older than 24 hours
                metrics.Cleanup();

                // Clear message processing queue if it's getting too full
                if (messageProcessingQueue.Count > 500)
                {
                    var itemsToRemove = messageProcessingQueue.Count - 100;
                    for (int i = 0; i < itemsToRemove; i++)
                    {
                        messageProcessingQueue.TryDequeue(out _);
                    }
                    OnDebugMessage($"Cleared {itemsToRemove} old items from processing queue");
                }

                // Force garbage collection if memory usage is high
                var memoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
                if (memoryMB > 500)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    OnDebugMessage($"Forced garbage collection - memory usage was {memoryMB}MB");
                }

                OnDebugMessage($"Cleanup completed - removed {keysToRemove.Count} old message hashes, memory: {memoryMB}MB");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Cleanup error: {ex.Message}");
            }
        }

        #endregion

        #region Enhanced Monitoring

        public async Task StartEnhancedMonitoring(List<ChannelInfo> channels)
        {
            try
            {
                OnDebugMessage($"Starting enhanced monitoring for {channels.Count} channels");

                // Stop any existing monitoring
                await StopMonitoring();

                // Initialize channel states
                foreach (var channel in channels)
                {
                    var state = new ChannelMonitoringState
                    {
                        ChannelId = channel.Id,
                        ChannelName = channel.Title,
                        AccessHash = channel.AccessHash,
                        Priority = DetermineChannelPriority(channel),
                        LastPollTime = DateTime.UtcNow,
                        LastMessageTime = DateTime.UtcNow
                    };

                    channelStates[channel.Id] = state;
                    monitoredChannels.Add(channel.Id);

                    // Get initial message ID
                    var latestId = await GetLatestMessageIdSafely(channel.Id, channel.AccessHash);
                    state.LastProcessedMessageId = latestId;
                }

                isMonitoring = true;

                // Start adaptive polling timer
                messagePollingTimer = new System.Threading.Timer(
                    async _ => await AdaptivePollingCycle(),
                    null,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(500) // Check every 500ms for high responsiveness
                );

                // Notify status change
                OnMonitoringStatusChanged(true, channels.Count, "Monitoring active");

                OnDebugMessage("Enhanced monitoring started successfully");
            }
            catch (Exception ex)
            {
                isMonitoring = false;
                OnErrorOccurred($"Failed to start monitoring: {ex.Message}");
                OnMonitoringStatusChanged(false, 0, $"Start failed: {ex.Message}");
            }
        }

        private ChannelPriority DetermineChannelPriority(ChannelInfo channel)
        {
            var title = channel.Title.ToLower();

            if (title.Contains("vip") || title.Contains("premium") || title.Contains("gold"))
                return ChannelPriority.High;

            if (title.Contains("signal") || title.Contains("forex") || title.Contains("crypto"))
                return ChannelPriority.Medium;

            return ChannelPriority.Low;
        }

        private async Task AdaptivePollingCycle()
        {
            if (!isMonitoring || client == null || !await pollingLock.WaitAsync(0))
                return;

            try
            {
                var sw = Stopwatch.StartNew();
                var now = DateTime.UtcNow;

                // Get channels that need polling based on adaptive intervals
                var channelsToPoll = channelStates.Values
                    .Where(state => ShouldPollChannel(state, now))
                    .OrderBy(state => state.Priority)
                    .ThenBy(state => state.LastPollTime)
                    .Take(10) // Process max 10 channels per cycle
                    .ToList();

                if (channelsToPoll.Any())
                {
                    // Use parallel processing with controlled concurrency
                    var tasks = channelsToPoll.Select(state =>
                        PollChannelWithRetry(state)
                    );

                    await Task.WhenAll(tasks);

                    sw.Stop();
                    metrics.RecordLatency("PollingCycle", sw.ElapsedMilliseconds);

                    OnDebugMessage($"Polled {channelsToPoll.Count} channels in {sw.ElapsedMilliseconds}ms");
                }
            }
            finally
            {
                pollingLock.Release();
            }
        }

        private bool ShouldPollChannel(ChannelMonitoringState state, DateTime now)
        {
            var timeSinceLastPoll = now - state.LastPollTime;
            var adaptiveInterval = GetAdaptiveInterval(state);

            return timeSinceLastPoll >= adaptiveInterval;
        }

        private TimeSpan GetAdaptiveInterval(ChannelMonitoringState state)
        {
            // Base intervals by priority
            var baseInterval = state.Priority switch
            {
                ChannelPriority.High => TimeSpan.FromSeconds(1),
                ChannelPriority.Medium => TimeSpan.FromSeconds(2),
                ChannelPriority.Low => TimeSpan.FromSeconds(5),
                _ => TimeSpan.FromSeconds(3)
            };

            // Adjust based on activity
            if (state.RecentMessageRate > 10) // Very active
                return TimeSpan.FromMilliseconds(500);

            if (state.RecentMessageRate > 5) // Active
                return baseInterval;

            if (state.RecentMessageRate > 1) // Moderate
                return baseInterval * 2;

            // Inactive - slow down polling
            if (state.ConsecutiveEmptyPolls > 10)
                return TimeSpan.FromSeconds(30);

            if (state.ConsecutiveEmptyPolls > 5)
                return TimeSpan.FromSeconds(10);

            return baseInterval * 3;
        }

        private async Task PollChannelWithRetry(ChannelMonitoringState state)
        {
            int retries = 0;
            Exception? lastException = null;

            while (retries < 3)
            {
                try
                {
                    await PollChannelOptimized(state);
                    state.ConsecutiveErrors = 0;
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    retries++;
                    state.ConsecutiveErrors++;

                    if (retries < 3)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100 * retries));
                    }
                }
            }

            OnErrorOccurred($"Failed to poll {state.ChannelName} after {retries} attempts: {lastException?.Message}");
        }

        private async Task PollChannelOptimized(ChannelMonitoringState state)
        {
            if (client == null) return;

            var sw = Stopwatch.StartNew();

            try
            {
                // Get channel history
                var inputPeer = await GetInputPeer(state.ChannelId, state.AccessHash);
                if (inputPeer == null)
                {
                    OnDebugMessage($"Could not get input peer for {state.ChannelName}");
                    return;
                }

                var history = await client.Messages_GetHistory(inputPeer, limit: 20);

                var messages = history.Messages
                    .OfType<TL.Message>()
                    .Where(m => m.ID > state.LastProcessedMessageId && !string.IsNullOrEmpty(m.message))
                    .OrderBy(m => m.ID)
                    .ToList();

                if (messages.Any())
                {
                    state.ConsecutiveEmptyPolls = 0;

                    foreach (var message in messages)
                    {
                        // Check for duplicate
                        var messageHash = GenerateMessageHash(message.message, state.ChannelId, message.ID);

                        if (!processedMessageHashes.ContainsKey(messageHash))
                        {
                            processedMessageHashes[messageHash] = DateTime.UtcNow;

                            // Queue for processing
                            messageProcessingQueue.Enqueue(new PendingMessage
                            {
                                Content = message.message,
                                ChannelId = state.ChannelId,
                                ChannelName = state.ChannelName,
                                MessageId = message.ID,
                                MessageDate = message.Date,
                                ReceivedAt = DateTime.UtcNow
                            });

                            state.MessageCount++;
                            state.LastMessageTime = DateTime.UtcNow;
                        }
                    }

                    state.LastProcessedMessageId = messages.Max(m => m.ID);

                    // Update message rate
                    var recentMessages = messages.Count(m => m.Date > DateTime.UtcNow.AddMinutes(-5));
                    state.RecentMessageRate = recentMessages / 5.0; // Messages per minute

                    OnDebugMessage($"Processed {messages.Count} new messages from {state.ChannelName}");
                }
                else
                {
                    state.ConsecutiveEmptyPolls++;
                }

                state.LastPollTime = DateTime.UtcNow;

                sw.Stop();
                metrics.RecordLatency($"Poll_{state.ChannelName}", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                throw new Exception($"Channel poll failed for {state.ChannelName}: {ex.Message}", ex);
            }
        }

        private async Task<InputPeer?> GetInputPeer(long channelId, long accessHash)
        {
            try
            {
                var dialogs = await client.Messages_GetAllDialogs();

                // Try as channel
                var channel = dialogs.chats.Values.OfType<Channel>().FirstOrDefault(c => c.ID == channelId);
                if (channel != null)
                    return new InputPeerChannel(channel.ID, channel.access_hash);

                // Try as chat
                var chat = dialogs.chats.Values.OfType<Chat>().FirstOrDefault(c => c.ID == channelId);
                if (chat != null)
                    return new InputPeerChat(chat.ID);

                // Try as user
                var user = dialogs.users.Values.FirstOrDefault(u => u.ID == channelId);
                if (user != null)
                    return new InputPeerUser(user.ID, user.access_hash);

                return null;
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to get input peer: {ex.Message}");
                return null;
            }
        }

        private async Task ProcessMessageSafely(PendingMessage pending)
        {
            try
            {
                var processingDelay = DateTime.UtcNow - pending.ReceivedAt;
                metrics.RecordLatency("ProcessingDelay", processingDelay.TotalMilliseconds);

                // Raise event for new signal
                OnNewSignalReceived(new SignalEventArgs
                {
                    Message = pending.Content,
                    ChannelId = pending.ChannelId,
                    ChannelName = pending.ChannelName,
                    MessageId = pending.MessageId,
                    MessageTime = pending.MessageDate,
                    ReceivedTime = pending.ReceivedAt,
                    ProcessedTime = DateTime.UtcNow
                });

                metrics.IncrementCounter("MessagesProcessed");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to process message: {ex.Message}");
                metrics.IncrementCounter("ProcessingErrors");
            }
        }

        #endregion

        #region Connection Management

        private async Task HandleConnectionFailure()
        {
            if (reconnectAttempts >= maxReconnectAttempts)
            {
                OnErrorOccurred("Maximum reconnection attempts reached. Stopping monitoring.");
                await StopMonitoring();
                return;
            }

            var delay = reconnectDelays[Math.Min(reconnectAttempts, reconnectDelays.Length - 1)];
            reconnectAttempts++;

            OnDebugMessage($"Attempting reconnection {reconnectAttempts}/{maxReconnectAttempts} in {delay.TotalSeconds}s");

            await Task.Delay(delay);

            try
            {
                if (!string.IsNullOrEmpty(phoneNumber))
                {
                    var wasMonitoring = isMonitoring;
                    var channelsToRestore = channelStates.Values.ToList();

                    await StopMonitoring();

                    if (await ConnectAsync(phoneNumber))
                    {
                        reconnectAttempts = 0;

                        if (wasMonitoring && channelsToRestore.Any())
                        {
                            var channelInfos = channelsToRestore.Select(s => new ChannelInfo
                            {
                                Id = s.ChannelId,
                                Title = s.ChannelName,
                                AccessHash = s.AccessHash
                            }).ToList();

                            await StartEnhancedMonitoring(channelInfos);
                        }

                        OnDebugMessage("Reconnection successful");
                    }
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Reconnection failed: {ex.Message}");
            }
        }

        private async Task<int> GetLatestMessageIdSafely(long channelId, long accessHash)
        {
            try
            {
                var inputPeer = await GetInputPeer(channelId, accessHash);
                if (inputPeer == null) return 0;

                var history = await client.Messages_GetHistory(inputPeer, limit: 1);
                var latestMessage = history.Messages.OfType<TL.Message>().FirstOrDefault();

                return latestMessage?.ID ?? 0;
            }
            catch (Exception ex)
            {
                OnDebugMessage($"Failed to get latest message ID: {ex.Message}");
                return 0;
            }
        }

        #endregion

        #region Utility Methods

        private string GenerateMessageHash(string content, long channelId, int messageId)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var input = $"{channelId}:{messageId}:{content}";
                var bytes = System.Text.Encoding.UTF8.GetBytes(input);
                var hash = md5.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }

        public async Task<MonitoringStatistics> GetMonitoringStatistics()
        {
            var stats = new MonitoringStatistics
            {
                IsActive = isMonitoring,
                MonitoredChannelsCount = monitoredChannels.Count,
                TotalMessagesProcessed = metrics.GetCounter("MessagesProcessed"),
                ProcessingErrors = metrics.GetCounter("ProcessingErrors"),
                AverageLatency = metrics.GetAverageLatency("PollingCycle"),
                ChannelStatuses = new List<ChannelStatus>()
            };

            foreach (var state in channelStates.Values)
            {
                stats.ChannelStatuses.Add(new ChannelStatus
                {
                    ChannelName = state.ChannelName,
                    MessageCount = state.MessageCount,
                    LastMessageTime = state.LastMessageTime,
                    Health = state.Health,
                    MessageRate = state.RecentMessageRate
                });
            }

            return stats;
        }

        public async Task StopMonitoring()
        {
            try
            {
                OnDebugMessage("Stopping enhanced monitoring...");

                isMonitoring = false;

                messagePollingTimer?.Dispose();
                messagePollingTimer = null;

                // Clear states
                channelStates.Clear();
                monitoredChannels.Clear();

                // Process remaining messages
                while (messageProcessingQueue.TryDequeue(out var pending))
                {
                    await ProcessMessageSafely(pending);
                }

                OnMonitoringStatusChanged(false, 0, "Monitoring stopped");
                OnDebugMessage("Enhanced monitoring stopped");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Error stopping monitoring: {ex.Message}");
            }
        }

        #endregion

        #region Events

        protected virtual void OnNewSignalReceived(SignalEventArgs e)
        {
            NewSignalReceived?.Invoke(this, e);
        }

        protected virtual void OnErrorOccurred(string error)
        {
            ErrorOccurred?.Invoke(this, error);
        }

        protected virtual void OnDebugMessage(string message)
        {
            DebugMessage?.Invoke(this, $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }

        protected virtual void OnMonitoringStatusChanged(bool isActive, int channelCount, string status)
        {
            MonitoringStatusChanged?.Invoke(this, new MonitoringStatusEventArgs
            {
                IsActive = isActive,
                ChannelCount = channelCount,
                Status = status,
                Timestamp = DateTime.UtcNow
            });
        }

        protected virtual void OnChannelHealthUpdated(long channelId, string channelName, ChannelHealth health)
        {
            ChannelHealthUpdated?.Invoke(this, new ChannelHealthEventArgs
            {
                ChannelId = channelId,
                ChannelName = channelName,
                Health = health,
                Timestamp = DateTime.UtcNow
            });
        }

        #endregion

        #region IDisposable

        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    isMonitoring = false;

                    messagePollingTimer?.Dispose();
                    healthCheckTimer?.Dispose();
                    cleanupTimer?.Dispose();

                    pollingLock?.Dispose();
                    processingLock?.Dispose();

                    client?.Dispose();
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }

    #region Supporting Classes

    public class ChannelMonitoringState
    {
        [JsonProperty("channelId")]
        public long ChannelId { get; set; }

        [JsonProperty("channelName")]
        public string ChannelName { get; set; } = "";

        [JsonProperty("accessHash")]
        public long AccessHash { get; set; }

        [JsonProperty("lastProcessedMessageId")]
        public int LastProcessedMessageId { get; set; }

        [JsonProperty("lastPollTime")]
        public DateTime LastPollTime { get; set; }

        [JsonProperty("lastMessageTime")]
        public DateTime LastMessageTime { get; set; }

        [JsonProperty("messageCount")]
        public int MessageCount { get; set; }

        [JsonProperty("consecutiveEmptyPolls")]
        public int ConsecutiveEmptyPolls { get; set; }

        [JsonProperty("consecutiveErrors")]
        public int ConsecutiveErrors { get; set; }

        [JsonProperty("recentMessageRate")]
        public double RecentMessageRate { get; set; }

        [JsonProperty("priority")]
        public ChannelPriority Priority { get; set; }

        [JsonProperty("health")]
        public ChannelHealth Health { get; set; } = ChannelHealth.Unknown;
    }

    public class PendingMessage
    {
        [JsonProperty("content")]
        public string Content { get; set; } = "";

        [JsonProperty("channelId")]
        public long ChannelId { get; set; }

        [JsonProperty("channelName")]
        public string ChannelName { get; set; } = "";

        [JsonProperty("messageId")]
        public int MessageId { get; set; }

        [JsonProperty("messageDate")]
        public DateTime MessageDate { get; set; }

        [JsonProperty("receivedAt")]
        public DateTime ReceivedAt { get; set; }
    }

    public class PerformanceMetrics
    {
        private readonly ConcurrentDictionary<string, List<double>> latencies = new();
        private readonly ConcurrentDictionary<string, long> counters = new();
        private readonly object lockObj = new object();

        public void RecordLatency(string operation, double milliseconds)
        {
            lock (lockObj)
            {
                if (!latencies.ContainsKey(operation))
                    latencies[operation] = new List<double>();

                var list = latencies[operation];
                list.Add(milliseconds);

                // Keep only last 1000 entries
                if (list.Count > 1000)
                    list.RemoveAt(0);
            }
        }

        public double GetAverageLatency(string operation)
        {
            lock (lockObj)
            {
                if (latencies.TryGetValue(operation, out var list) && list.Any())
                    return list.Average();
                return 0;
            }
        }

        public void IncrementCounter(string name)
        {
            counters.AddOrUpdate(name, 1, (_, value) => value + 1);
        }

        public long GetCounter(string name)
        {
            return counters.GetValueOrDefault(name, 0);
        }

        public void Cleanup()
        {
            lock (lockObj)
            {
                // Remove old metrics
                foreach (var kvp in latencies.ToList())
                {
                    if (kvp.Value.Count == 0)
                        latencies.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    public class SignalEventArgs : EventArgs
    {
        [JsonProperty("message")]
        public string Message { get; set; } = "";

        [JsonProperty("channelId")]
        public long ChannelId { get; set; }

        [JsonProperty("channelName")]
        public string ChannelName { get; set; } = "";

        [JsonProperty("messageId")]
        public int MessageId { get; set; }

        [JsonProperty("messageTime")]
        public DateTime MessageTime { get; set; }

        [JsonProperty("receivedTime")]
        public DateTime ReceivedTime { get; set; }

        [JsonProperty("processedTime")]
        public DateTime ProcessedTime { get; set; }
    }

    public class MonitoringStatusEventArgs : EventArgs
    {
        public bool IsActive { get; set; }
        public int ChannelCount { get; set; }
        public string Status { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    public class ChannelHealthEventArgs : EventArgs
    {
        [JsonProperty("channelId")]
        public long ChannelId { get; set; }

        [JsonProperty("channelName")]
        public string ChannelName { get; set; } = "";

        [JsonProperty("health")]
        public ChannelHealth Health { get; set; }

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    public class MonitoringStatistics
    {
        public bool IsActive { get; set; }
        public int MonitoredChannelsCount { get; set; }
        public long TotalMessagesProcessed { get; set; }
        public long ProcessingErrors { get; set; }
        public double AverageLatency { get; set; }
        public List<ChannelStatus> ChannelStatuses { get; set; } = new();
    }

    public class ChannelStatus
    {
        [JsonProperty("channelName")]
        public string ChannelName { get; set; } = "";

        [JsonProperty("messageCount")]
        public int MessageCount { get; set; }

        [JsonProperty("lastMessageTime")]
        public DateTime LastMessageTime { get; set; }

        [JsonProperty("health")]
        public ChannelHealth Health { get; set; }

        [JsonProperty("messageRate")]
        public double MessageRate { get; set; }
    }

    public enum ChannelPriority
    {
        Low = 0,
        Medium = 1,
        High = 2
    }

    public enum ChannelHealth
    {
        Unknown = 0,
        Healthy = 1,
        Inactive = 2,
        Warning = 3,
        Critical = 4
    }

    #endregion
}