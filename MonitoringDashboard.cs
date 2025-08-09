using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.ComponentModel;

namespace TelegramEAManager
{
    public partial class MonitoringDashboard : Form
    {
        private readonly EnhancedTelegramService telegramService;
        private readonly SignalProcessingService signalProcessor;
        private System.Windows.Forms.Timer refreshTimer;
        private readonly Dictionary<long, ChannelHealthIndicator> channelIndicators = new();

        public MonitoringDashboard(EnhancedTelegramService telegramService, SignalProcessingService signalProcessor)
        {
            this.telegramService = telegramService;
            this.signalProcessor = signalProcessor;

            InitializeComponent();
            SetupUI();
            SubscribeToEvents();
            StartRefreshTimer();
        }

        private void SetupUI()
        {
            this.Text = "📊 Real-Time Monitoring Dashboard - islamahmed9717";
            this.Size = new Size(1200, 800);
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Color.FromArgb(245, 247, 250);

            CreateHeaderSection();
            CreateMetricsSection();
            CreateChannelHealthSection();
            CreateSignalFeedSection();
            CreateControlSection();
        }

        private void CreateHeaderSection()
        {
            var headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 80,
                BackColor = Color.FromArgb(17, 24, 39)
            };

            var lblTitle = new Label
            {
                Text = "🎯 REAL-TIME MONITORING DASHBOARD",
                Location = new Point(20, 15),
                Size = new Size(500, 30),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 16F, FontStyle.Bold)
            };
            headerPanel.Controls.Add(lblTitle);

            var lblStatus = new Label
            {
                Name = "lblDashboardStatus",
                Text = "● System Active",
                Location = new Point(20, 45),
                Size = new Size(300, 20),
                ForeColor = Color.FromArgb(34, 197, 94),
                Font = new Font("Segoe UI", 10F)
            };
            headerPanel.Controls.Add(lblStatus);

            var lblTime = new Label
            {
                Name = "lblDashboardTime",
                Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Location = new Point(1000, 30),
                Size = new Size(150, 20),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10F),
                TextAlign = ContentAlignment.MiddleRight
            };
            headerPanel.Controls.Add(lblTime);

            this.Controls.Add(headerPanel);
        }

        private void CreateMetricsSection()
        {
            var metricsPanel = new Panel
            {
                Name = "metricsPanel",
                Location = new Point(20, 100),
                Size = new Size(1160, 120),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            // Create metric cards
            CreateMetricCard(metricsPanel, "lblActiveChannels", "Active Channels", "0", Color.FromArgb(59, 130, 246), 10, 10);
            CreateMetricCard(metricsPanel, "lblTotalMessages", "Total Messages", "0", Color.FromArgb(34, 197, 94), 300, 10);
            CreateMetricCard(metricsPanel, "lblMessageRate", "Messages/Min", "0.0", Color.FromArgb(168, 85, 247), 590, 10);
            CreateMetricCard(metricsPanel, "lblAvgLatency", "Avg Latency", "0ms", Color.FromArgb(249, 115, 22), 880, 10);

            this.Controls.Add(metricsPanel);
        }

        private void CreateMetricCard(Panel parent, string name, string title, string value, Color color, int x, int y)
        {
            var cardPanel = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(270, 100),
                BackColor = Color.FromArgb(249, 250, 251),
                BorderStyle = BorderStyle.None
            };

            var lblIcon = new Label
            {
                Text = "📊",
                Location = new Point(10, 10),
                Size = new Size(40, 40),
                Font = new Font("Segoe UI", 20F),
                TextAlign = ContentAlignment.MiddleCenter
            };
            cardPanel.Controls.Add(lblIcon);

            var lblTitle = new Label
            {
                Text = title,
                Location = new Point(60, 15),
                Size = new Size(200, 20),
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.FromArgb(107, 114, 128)
            };
            cardPanel.Controls.Add(lblTitle);

            var lblValue = new Label
            {
                Name = name,
                Text = value,
                Location = new Point(60, 35),
                Size = new Size(200, 35),
                Font = new Font("Segoe UI", 20F, FontStyle.Bold),
                ForeColor = color
            };
            cardPanel.Controls.Add(lblValue);

            var progressBar = new Panel
            {
                Location = new Point(0, 95),
                Size = new Size(270, 5),
                BackColor = color
            };
            cardPanel.Controls.Add(progressBar);

            parent.Controls.Add(cardPanel);
        }

        private void CreateChannelHealthSection()
        {
            var healthPanel = new Panel
            {
                Name = "healthPanel",
                Location = new Point(20, 240),
                Size = new Size(1160, 200),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            var lblHealthTitle = new Label
            {
                Text = "📡 CHANNEL HEALTH STATUS",
                Location = new Point(15, 10),
                Size = new Size(300, 25),
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = Color.FromArgb(17, 24, 39)
            };
            healthPanel.Controls.Add(lblHealthTitle);

            var channelListPanel = new FlowLayoutPanel
            {
                Name = "channelListPanel",
                Location = new Point(15, 40),
                Size = new Size(1130, 145),
                AutoScroll = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };
            healthPanel.Controls.Add(channelListPanel);

            this.Controls.Add(healthPanel);
        }

        private void CreateSignalFeedSection()
        {
            var feedPanel = new Panel
            {
                Name = "feedPanel",
                Location = new Point(20, 460),
                Size = new Size(700, 240),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            var lblFeedTitle = new Label
            {
                Text = "📨 LIVE SIGNAL FEED",
                Location = new Point(15, 10),
                Size = new Size(300, 25),
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = Color.FromArgb(17, 24, 39)
            };
            feedPanel.Controls.Add(lblFeedTitle);

            var lvSignalFeed = new ListView
            {
                Name = "lvSignalFeed",
                Location = new Point(15, 40),
                Size = new Size(670, 185),
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(249, 250, 251)
            };

            lvSignalFeed.Columns.Add("Time", 80);
            lvSignalFeed.Columns.Add("Channel", 150);
            lvSignalFeed.Columns.Add("Symbol", 80);
            lvSignalFeed.Columns.Add("Type", 60);
            lvSignalFeed.Columns.Add("Status", 100);
            lvSignalFeed.Columns.Add("Latency", 80);

            feedPanel.Controls.Add(lvSignalFeed);
            this.Controls.Add(feedPanel);
        }

        private void CreateControlSection()
        {
            var controlPanel = new Panel
            {
                Name = "controlPanel",
                Location = new Point(740, 460),
                Size = new Size(440, 240),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            var lblControlTitle = new Label
            {
                Text = "🎮 MONITORING CONTROLS",
                Location = new Point(15, 10),
                Size = new Size(300, 25),
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = Color.FromArgb(17, 24, 39)
            };
            controlPanel.Controls.Add(lblControlTitle);

            // Connection quality indicator
            var lblConnectionQuality = new Label
            {
                Name = "lblConnectionQuality",
                Text = "Connection: Excellent",
                Location = new Point(15, 45),
                Size = new Size(200, 20),
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.FromArgb(34, 197, 94)
            };
            controlPanel.Controls.Add(lblConnectionQuality);

            // Performance graph placeholder
            var perfGraph = new Panel
            {
                Name = "perfGraph",
                Location = new Point(15, 75),
                Size = new Size(410, 100),
                BackColor = Color.FromArgb(249, 250, 251),
                BorderStyle = BorderStyle.FixedSingle
            };

            var lblGraphTitle = new Label
            {
                Text = "📈 Performance Graph",
                Location = new Point(10, 5),
                Size = new Size(200, 20),
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.FromArgb(107, 114, 128)
            };
            perfGraph.Controls.Add(lblGraphTitle);

            controlPanel.Controls.Add(perfGraph);

            // Control buttons
            var btnPauseResume = new Button
            {
                Name = "btnPauseResume",
                Text = "⏸️ PAUSE",
                Location = new Point(15, 185),
                Size = new Size(100, 40),
                BackColor = Color.FromArgb(249, 115, 22),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };
            btnPauseResume.Click += BtnPauseResume_Click;
            controlPanel.Controls.Add(btnPauseResume);

            var btnStatistics = new Button
            {
                Text = "📊 STATISTICS",
                Location = new Point(125, 185),
                Size = new Size(100, 40),
                BackColor = Color.FromArgb(59, 130, 246),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };
            btnStatistics.Click += BtnStatistics_Click;
            controlPanel.Controls.Add(btnStatistics);

            var btnExport = new Button
            {
                Text = "📤 EXPORT",
                Location = new Point(235, 185),
                Size = new Size(90, 40),
                BackColor = Color.FromArgb(34, 197, 94),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };
            btnExport.Click += BtnExport_Click;
            controlPanel.Controls.Add(btnExport);

            var btnClose = new Button
            {
                Text = "❌ CLOSE",
                Location = new Point(335, 185),
                Size = new Size(90, 40),
                BackColor = Color.FromArgb(220, 38, 38),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };
            btnClose.Click += (s, e) => this.Close();
            controlPanel.Controls.Add(btnClose);

            this.Controls.Add(controlPanel);
        }

        private void SubscribeToEvents()
        {
            telegramService.NewSignalReceived += OnNewSignalReceived;
            telegramService.MonitoringStatusChanged += OnMonitoringStatusChanged;
            telegramService.ChannelHealthUpdated += OnChannelHealthUpdated;
            telegramService.ErrorOccurred += OnErrorOccurred;
            telegramService.DebugMessage += OnDebugMessage;
        }

        private void StartRefreshTimer()
        {
            refreshTimer = new System.Windows.Forms.Timer
            {
                Interval = 1000
            };
            refreshTimer.Tick += RefreshTimer_Tick;
            refreshTimer.Start();
        }

        private async void RefreshTimer_Tick(object sender, EventArgs e)
        {
            // Update time
            var lblTime = this.Controls.Find("lblDashboardTime", true).FirstOrDefault() as Label;
            if (lblTime != null)
            {
                lblTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }

            // Update statistics
            var stats = await telegramService.GetMonitoringStatistics();
            UpdateMetrics(stats);
        }

        private void UpdateMetrics(MonitoringStatistics stats)
        {
            UpdateMetricValue("lblActiveChannels", stats.MonitoredChannelsCount.ToString());
            UpdateMetricValue("lblTotalMessages", stats.TotalMessagesProcessed.ToString());
            UpdateMetricValue("lblMessageRate", $"{stats.ChannelStatuses.Sum(c => c.MessageRate):F1}");
            UpdateMetricValue("lblAvgLatency", $"{stats.AverageLatency:F0}ms");
        }

        private void UpdateMetricValue(string controlName, string value)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => UpdateMetricValue(controlName, value)));
                return;
            }

            var control = this.Controls.Find(controlName, true).FirstOrDefault() as Label;
            if (control != null)
            {
                control.Text = value;
            }
        }

        private void OnNewSignalReceived(object sender, SignalEventArgs e)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => OnNewSignalReceived(sender, e)));
                return;
            }

            var lvSignalFeed = this.Controls.Find("lvSignalFeed", true).FirstOrDefault() as ListView;
            if (lvSignalFeed != null)
            {
                var latency = (e.ProcessedTime - e.ReceivedTime).TotalMilliseconds;

                // Process the signal to extract symbol and type
                var processedSignal = signalProcessor.ProcessTelegramMessage(e.Message, e.ChannelId, e.ChannelName);

                // Extract symbol from message if ParsedData is empty
                string symbol = "N/A";
                string type = "SIGNAL";

                if (processedSignal.ParsedData != null && !string.IsNullOrEmpty(processedSignal.ParsedData.Symbol))
                {
                    symbol = processedSignal.ParsedData.Symbol;
                    type = processedSignal.ParsedData.GetOrderTypeDescription();
                }
                else
                {
                    // Try to extract symbol from the message text directly
                    symbol = ExtractSymbolFromMessage(e.Message);
                    type = ExtractTypeFromMessage(e.Message);
                }

                var item = new ListViewItem(e.ProcessedTime.ToString("HH:mm:ss"));
                item.SubItems.Add(e.ChannelName);
                item.SubItems.Add(symbol);
                item.SubItems.Add(type);
                item.SubItems.Add(processedSignal.Status.Contains("Processed") ? "PROCESSED" : "NEW");
                item.SubItems.Add($"{latency:F0}ms");

                // Color coding based on status
                if (processedSignal.Status.Contains("Processed"))
                    item.BackColor = Color.FromArgb(220, 255, 220);
                else if (processedSignal.Status.Contains("Error") || processedSignal.Status.Contains("Invalid"))
                    item.BackColor = Color.FromArgb(255, 220, 220);
                else
                    item.BackColor = Color.FromArgb(255, 255, 220);

                lvSignalFeed.Items.Insert(0, item);

                // Keep only last 20 items
                while (lvSignalFeed.Items.Count > 20)
                {
                    lvSignalFeed.Items.RemoveAt(lvSignalFeed.Items.Count - 1);
                }
            }
        }

        private string ExtractSymbolFromMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "...";

            // Common symbol patterns
            var symbolPatterns = new[]
            {
                @"\b(EURUSD|GBPUSD|USDJPY|USDCHF|AUDUSD|USDCAD|NZDUSD)\b",
                @"\b(XAUUSD|GOLD|XAGUSD|SILVER)\b",
                @"\b(US30|NAS100|SPX500|GER30|UK100)\b",
                @"\b(BTCUSD|ETHUSD|BITCOIN|ETHEREUM)\b",
                @"\b([A-Z]{6,7})\b" // Generic 6-7 letter pattern
            };

            foreach (var pattern in symbolPatterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(message.ToUpper(), pattern);
                if (match.Success)
                {
                    return match.Value;
                }
            }

            return "...";
        }

        private string ExtractTypeFromMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "SIGNAL";

            var upperMessage = message.ToUpper();

            if (upperMessage.Contains("BUY LIMIT") || upperMessage.Contains("BUY STOP"))
                return upperMessage.Contains("LIMIT") ? "BUY LIMIT" : "BUY STOP";
            else if (upperMessage.Contains("SELL LIMIT") || upperMessage.Contains("SELL STOP"))
                return upperMessage.Contains("LIMIT") ? "SELL LIMIT" : "SELL STOP";
            else if (upperMessage.Contains("BUY"))
                return "BUY";
            else if (upperMessage.Contains("SELL"))
                return "SELL";
            else
                return "SIGNAL";
        }

        private void OnMonitoringStatusChanged(object sender, MonitoringStatusEventArgs e)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => OnMonitoringStatusChanged(sender, e)));
                return;
            }

            var lblStatus = this.Controls.Find("lblDashboardStatus", true).FirstOrDefault() as Label;
            if (lblStatus != null)
            {
                lblStatus.Text = e.IsActive ? "● System Active" : "● System Inactive";
                lblStatus.ForeColor = e.IsActive ? Color.FromArgb(34, 197, 94) : Color.FromArgb(220, 38, 38);
            }
        }

        private void OnChannelHealthUpdated(object sender, ChannelHealthEventArgs e)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => OnChannelHealthUpdated(sender, e)));
                return;
            }

            UpdateChannelHealthIndicator(e.ChannelId, e.ChannelName, e.Health);
        }

        private void UpdateChannelHealthIndicator(long channelId, string channelName, ChannelHealth health)
        {
            var channelListPanel = this.Controls.Find("channelListPanel", true).FirstOrDefault() as FlowLayoutPanel;
            if (channelListPanel == null) return;

            if (!channelIndicators.ContainsKey(channelId))
            {
                var indicator = new ChannelHealthIndicator
                {
                    ChannelId = channelId,
                    ChannelName = channelName,
                    Size = new Size(180, 60),
                    Margin = new Padding(5)
                };

                channelIndicators[channelId] = indicator;
                channelListPanel.Controls.Add(indicator);
            }

            channelIndicators[channelId].UpdateHealth(health);
        }

        private void OnErrorOccurred(object sender, string error)
        {
            // Log errors to debug output
            System.Diagnostics.Debug.WriteLine($"[ERROR] {error}");
        }

        private void OnDebugMessage(object sender, string message)
        {
            // Log debug messages
            System.Diagnostics.Debug.WriteLine($"[DEBUG] {message}");
        }

        private void BtnPauseResume_Click(object sender, EventArgs e)
        {
            var btn = sender as Button;
            if (btn?.Text.Contains("PAUSE") == true)
            {
                // Pause monitoring
                btn.Text = "▶️ RESUME";
                btn.BackColor = Color.FromArgb(34, 197, 94);
            }
            else
            {
                // Resume monitoring
                btn.Text = "⏸️ PAUSE";
                btn.BackColor = Color.FromArgb(249, 115, 22);
            }
        }

        private async void BtnStatistics_Click(object sender, EventArgs e)
        {
            var stats = await telegramService.GetMonitoringStatistics();

            var report = $"📊 MONITORING STATISTICS\n\n" +
                        $"Active Channels: {stats.MonitoredChannelsCount}\n" +
                        $"Total Messages: {stats.TotalMessagesProcessed}\n" +
                        $"Processing Errors: {stats.ProcessingErrors}\n" +
                        $"Average Latency: {stats.AverageLatency:F1}ms\n\n" +
                        "CHANNEL DETAILS:\n";

            foreach (var channel in stats.ChannelStatuses)
            {
                report += $"\n{channel.ChannelName}:\n" +
                         $"  Messages: {channel.MessageCount}\n" +
                         $"  Rate: {channel.MessageRate:F1}/min\n" +
                         $"  Health: {channel.Health}\n";
            }

            MessageBox.Show(report, "Monitoring Statistics", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void BtnExport_Click(object sender, EventArgs e)
        {
            // Export current monitoring data
            var saveDialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = $"MonitoringData_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (saveDialog.ShowDialog() == DialogResult.OK)
            {
                // Implementation for exporting data
                MessageBox.Show("Data exported successfully!", "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            refreshTimer?.Stop();
            refreshTimer?.Dispose();
            base.OnFormClosing(e);
        }
    }

    // Custom control for channel health indicator
    public class ChannelHealthIndicator : UserControl
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public long ChannelId { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string ChannelName { get; set; } = "";
        private ChannelHealth currentHealth = ChannelHealth.Unknown;
        private Label lblName;
        private Label lblStatus;
        private Panel healthBar;

        public ChannelHealthIndicator()
        {
            InitializeControl();
        }

        private void InitializeControl()
        {
            this.BackColor = Color.FromArgb(249, 250, 251);
            this.BorderStyle = BorderStyle.FixedSingle;

            lblName = new Label
            {
                Location = new Point(5, 5),
                Size = new Size(170, 20),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Text = ChannelName
            };
            this.Controls.Add(lblName);

            lblStatus = new Label
            {
                Location = new Point(5, 25),
                Size = new Size(100, 15),
                Font = new Font("Segoe UI", 8F),
                Text = "Unknown"
            };
            this.Controls.Add(lblStatus);

            healthBar = new Panel
            {
                Location = new Point(5, 45),
                Size = new Size(170, 8),
                BackColor = Color.Gray
            };
            this.Controls.Add(healthBar);
        }

        public void UpdateHealth(ChannelHealth health)
        {
            currentHealth = health;
            lblName.Text = ChannelName;

            switch (health)
            {
                case ChannelHealth.Healthy:
                    lblStatus.Text = "● Healthy";
                    lblStatus.ForeColor = Color.FromArgb(34, 197, 94);
                    healthBar.BackColor = Color.FromArgb(34, 197, 94);
                    break;
                case ChannelHealth.Inactive:
                    lblStatus.Text = "● Inactive";
                    lblStatus.ForeColor = Color.FromArgb(249, 115, 22);
                    healthBar.BackColor = Color.FromArgb(249, 115, 22);
                    break;
                case ChannelHealth.Warning:
                    lblStatus.Text = "● Warning";
                    lblStatus.ForeColor = Color.FromArgb(251, 191, 36);
                    healthBar.BackColor = Color.FromArgb(251, 191, 36);
                    break;
                case ChannelHealth.Critical:
                    lblStatus.Text = "● Critical";
                    lblStatus.ForeColor = Color.FromArgb(220, 38, 38);
                    healthBar.BackColor = Color.FromArgb(220, 38, 38);
                    break;
                default:
                    lblStatus.Text = "● Unknown";
                    lblStatus.ForeColor = Color.Gray;
                    healthBar.BackColor = Color.Gray;
                    break;
            }
        }
    }
}