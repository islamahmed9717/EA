using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Threading;

namespace TelegramEAManager
{
    public partial class Form1 : Form
    {
        #region Private Fields
        //private TelegramService telegramService = null!;
        private EnhancedTelegramService telegramService = null!;
        private MonitoringDashboard? monitoringDashboard = null;

        private SignalProcessingService signalProcessor = null!;
        private List<ChannelInfo> allChannels = new List<ChannelInfo>();
        private List<ChannelInfo> selectedChannels = new List<ChannelInfo>();
        private bool isMonitoring = false;
        private System.Windows.Forms.Timer uiUpdateTimer = null!;
        private List<ProcessedSignal> allSignals = new List<ProcessedSignal>();
        private FileSystemWatcher? signalFileWatcher;
        private System.Windows.Forms.Timer? cleanupTimer;
        private readonly HashSet<string> liveFeedIds = new HashSet<string>();

        private Form? debugForm = null;
        private bool isDebugFormOpen = false;
        private TextBox? debugConsole = null;
        private readonly object debugLock = new object();

        private readonly ConcurrentDictionary<string, DateTime> recentSignalsInUI = new ConcurrentDictionary<string, DateTime>();
        private readonly SemaphoreSlim uiUpdateSemaphore = new SemaphoreSlim(1, 1);
        private System.Threading.Timer? uiCleanupTimer;

        // Performance optimization fields
        private readonly object liveFeedLock = new object();
        private readonly Queue<ProcessedSignal> pendingSignals = new Queue<ProcessedSignal>();
        private System.Windows.Forms.Timer? liveFeedUpdateTimer;
        private DateTime lastUIUpdate = DateTime.MinValue;
        private CancellationTokenSource? monitoringCts;
        private readonly PerformanceMonitor perfMonitor = new PerformanceMonitor();
        private readonly Dictionary<string, List<ChannelInfo>> channelCache = new Dictionary<string, List<ChannelInfo>>();
        private DateTime lastChannelLoadTime = DateTime.MinValue;
        private string phoneNumber = "";
        private EASettings eaSettings = new EASettings();
        #endregion

        public Form1()
        {
            InitializeComponent();
            InitializeServices();
            SetupUI();

            // Add optimization calls
            OptimizeListViews();
            SetupGlobalExceptionHandling();

            // Enable form-level double buffering
            this.SetStyle(ControlStyles.AllPaintingInWmPaint |
                          ControlStyles.UserPaint |
                          ControlStyles.DoubleBuffer, true);

            LoadApplicationSettings();
            SetupTimers();

            // Start health monitoring in background
            Task.Run(() => StartHealthMonitoring());
        }

        #region Performance Monitor Class
        private class PerformanceMonitor
        {
            private readonly Dictionary<string, List<long>> metrics = new Dictionary<string, List<long>>();
            private readonly System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();

            public void StartTimer(string operation)
            {
                stopwatch.Restart();
            }

            public void EndTimer(string operation)
            {
                stopwatch.Stop();

                if (!metrics.ContainsKey(operation))
                    metrics[operation] = new List<long>();

                metrics[operation].Add(stopwatch.ElapsedMilliseconds);

                if (metrics[operation].Count > 100)
                    metrics[operation].RemoveAt(0);
            }

            public string GetReport()
            {
                var report = new StringBuilder();
                report.AppendLine("Performance Metrics:");

                foreach (var kvp in metrics)
                {
                    if (kvp.Value.Count > 0)
                    {
                        var avg = kvp.Value.Average();
                        var max = kvp.Value.Max();
                        var min = kvp.Value.Min();

                        report.AppendLine($"  {kvp.Key}: Avg={avg:F1}ms, Min={min}ms, Max={max}ms");
                    }
                }

                return report.ToString();
            }
        }
        #endregion

        #region Initialization Methods
        private void InitializeServices()
        {
            telegramService = new EnhancedTelegramService();
            signalProcessor = new SignalProcessingService();

            // Subscribe to real-time message events
            telegramService.NewSignalReceived += TelegramService_EnhancedNewSignalReceived;
            telegramService.ErrorOccurred += TelegramService_ErrorOccurred;
            telegramService.DebugMessage += TelegramService_DebugMessage;
            telegramService.MonitoringStatusChanged += TelegramService_MonitoringStatusChanged;
            telegramService.ChannelHealthUpdated += TelegramService_ChannelHealthUpdated;


            // Subscribe to signal processing events
            signalProcessor.SignalProcessed += SignalProcessor_SignalProcessed;
            signalProcessor.ErrorOccurred += SignalProcessor_ErrorOccurred;
            signalProcessor.DebugMessage += SignalProcessor_DebugMessage;

            // Start UI cleanup timer with optimized interval
            uiCleanupTimer = new System.Threading.Timer(
                _ => CleanupRecentSignalsTracker(),
                null,
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(2)
            );

            // Enable double buffering for smoother UI
            this.SetStyle(ControlStyles.AllPaintingInWmPaint |
                          ControlStyles.UserPaint |
                          ControlStyles.DoubleBuffer, true);
        }

        private void TelegramService_ChannelHealthUpdated(object? sender, ChannelHealthEventArgs e)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => TelegramService_ChannelHealthUpdated(sender, e)));
                return;
            }

            // Update channel health in selected channels list
            var lvSelected = this.Controls.Find("lvSelected", true).FirstOrDefault() as ListView;
            if (lvSelected != null)
            {
                foreach (ListViewItem item in lvSelected.Items)
                {
                    var channel = item.Tag as ChannelInfo;
                    if (channel != null && channel.Id == e.ChannelId)
                    {
                        // Update status column with health
                        if (item.SubItems.Count > 3)
                        {
                            item.SubItems[3].Text = $"📊 {e.Health}";

                            // Color based on health
                            item.BackColor = e.Health switch
                            {
                                ChannelHealth.Healthy => Color.FromArgb(220, 255, 220),
                                ChannelHealth.Warning => Color.FromArgb(255, 255, 220),
                                ChannelHealth.Critical => Color.FromArgb(255, 220, 220),
                                _ => Color.White
                            };
                        }
                        break;
                    }
                }
            }
        }

        private void TelegramService_MonitoringStatusChanged(object? sender, MonitoringStatusEventArgs e)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => TelegramService_MonitoringStatusChanged(sender, e)));
                return;
            }

            LogMessage($"📊 Monitoring status: {e.Status} - Channels: {e.ChannelCount}");
            UpdateStatus(telegramService.IsUserAuthorized(), e.IsActive);
        }

        private void TelegramService_EnhancedNewSignalReceived(object? sender, SignalEventArgs e)
        {
            try
            {
                perfMonitor.StartTimer("ProcessMessage");

                // Process in background to avoid UI blocking
                var processedSignal = signalProcessor.ProcessTelegramMessage(
                    e.Message, e.ChannelId, e.ChannelName
                );

                // Update UI on UI thread
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke(new Action(() => {
                        if (!processedSignal.Status.Contains("Duplicate"))
                        {
                            UpdateUIAfterSignal(processedSignal, e.ChannelId, e.ChannelName);
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Error processing message: {ex.Message}");
            }
            finally
            {
                perfMonitor.EndTimer("ProcessMessage");
            }
        }

        private void SetupTimers()
        {
            uiUpdateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            uiUpdateTimer.Tick += UiUpdateTimer_Tick;
            uiUpdateTimer.Start();
        }

        private void SetupGlobalExceptionHandling()
        {
            Application.ThreadException += (s, e) =>
            {
                LogMessage($"❌ Thread exception: {e.Exception.Message}");
                RecoverFromError(e.Exception).Wait(5000);
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                LogMessage($"❌ Unhandled exception: {ex?.Message ?? "Unknown error"}");

                if (!e.IsTerminating)
                {
                    RecoverFromError(ex ?? new Exception("Unknown error")).Wait(5000);
                }
            };
        }

        private void OptimizeListViews()
        {
            var lvChannels = this.Controls.Find("lvChannels", true).FirstOrDefault() as ListView;
            var lvSelected = this.Controls.Find("lvSelected", true).FirstOrDefault() as ListView;
            var lvLiveSignals = this.Controls.Find("lvLiveSignals", true).FirstOrDefault() as ListView;

            if (lvChannels != null)
            {
                SetupOptimizedListView(lvChannels, false);
            }

            if (lvSelected != null)
            {
                SetupOptimizedListView(lvSelected, false);
            }

            if (lvLiveSignals != null)
            {
                SetupOptimizedListView(lvLiveSignals, allSignals.Count > 1000);
            }
        }

        private void SetupOptimizedListView(ListView lv, bool useVirtualMode = false)
        {
            if (useVirtualMode)
            {
                lv.VirtualMode = true;
                lv.VirtualListSize = 0;
                lv.RetrieveVirtualItem += (s, e) => {
                    if (e.ItemIndex >= 0 && e.ItemIndex < allSignals.Count)
                    {
                        var signal = allSignals[e.ItemIndex];
                        e.Item = CreateListViewItem(signal);
                    }
                };
            }

            // Enable double buffering for smoother rendering
            typeof(ListView).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                null, lv, new object[] { true });

            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.GridLines = true;
        }

        // 5. UPDATE CreateListViewItem to show order type
        private ListViewItem CreateListViewItem(ProcessedSignal signal)
        {
            var localTime = signal.DateTime.ToLocalTime();
            var item = new ListViewItem(localTime.ToString("HH:mm:ss"));
            item.SubItems.Add(signal.ChannelName);
            item.SubItems.Add(signal.ParsedData?.Symbol ?? "N/A");
            item.SubItems.Add(signal.ParsedData?.GetOrderTypeDescription() ?? "N/A"); // Order type
            item.SubItems.Add(signal.ParsedData?.StopLoss > 0 ? signal.ParsedData.StopLoss.ToString("F5") : "N/A");
            item.SubItems.Add(signal.ParsedData?.TakeProfit1 > 0 ? signal.ParsedData.TakeProfit1.ToString("F5") : "N/A");
            item.SubItems.Add(signal.Status);

            // Color coding
            if (signal.Status.Contains("Processed"))
                item.BackColor = Color.FromArgb(220, 255, 220);
            else if (signal.Status.Contains("Error") || signal.Status.Contains("Invalid"))
                item.BackColor = Color.FromArgb(255, 220, 220);
            else if (signal.Status.Contains("Ignored"))
                item.BackColor = Color.FromArgb(255, 255, 220);
            else if (signal.Status.Contains("Test"))
                item.BackColor = Color.FromArgb(220, 220, 255);

            // Special color for pending orders
            if (signal.ParsedData?.OrderType != "MARKET")
                item.Font = new Font(item.Font, FontStyle.Italic);

            item.Tag = signal;
            return item;
        }
        #endregion

        #region UI Setup
        private void SetupUI()
        {
            this.Text = "📊 Telegram EA Manager - islamahmed9717 | Real Implementation";
            this.Size = new Size(1400, 900);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(245, 245, 245);
            this.WindowState = FormWindowState.Normal;

            CreateHeaderPanel();
            CreateMainContent();
            CreateBottomPanel();
            CreateStatusBar();
        }

        private void CreateHeaderPanel()
        {
            var headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 65,
                BackColor = Color.FromArgb(37, 99, 235)
            };
            this.Controls.Add(headerPanel);

            var lblTitle = new Label
            {
                Text = "📊 REAL TELEGRAM EA MANAGER",
                Location = new Point(20, 5),
                Size = new Size(400, 25),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 14F, FontStyle.Bold)
            };
            headerPanel.Controls.Add(lblTitle);

            var lblSubtitle = new Label
            {
                Name = "lblSubtitle",
                Text = $"🕒 Current Now (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} | User: islamahmed9717",
                Location = new Point(20, 28),
                Size = new Size(500, 15),
                ForeColor = Color.FromArgb(200, 220, 255),
                Font = new Font("Segoe UI", 8F)
            };
            headerPanel.Controls.Add(lblSubtitle);

            // Phone section
            var lblPhone = new Label
            {
                Text = "📱 Phone:",
                Location = new Point(550, 5),
                Size = new Size(70, 15),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold)
            };
            headerPanel.Controls.Add(lblPhone);

            var cmbPhone = new ComboBox
            {
                Name = "cmbPhone",
                Location = new Point(620, 3),
                Size = new Size(180, 20),
                Font = new Font("Segoe UI", 9F),
                DropDownStyle = ComboBoxStyle.DropDown
            };
            headerPanel.Controls.Add(cmbPhone);

            var btnConnect = new Button
            {
                Name = "btnConnect",
                Text = "🔗 CONNECT",
                Location = new Point(810, 3),
                Size = new Size(100, 20),
                BackColor = Color.FromArgb(34, 197, 94),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold)
            };
            btnConnect.Click += BtnConnect_Click;
            headerPanel.Controls.Add(btnConnect);

            // MT4 Path
            var lblMT4 = new Label
            {
                Text = "📁 MT4/MT5:",
                Location = new Point(550, 28),
                Size = new Size(70, 15),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8F)
            };
            headerPanel.Controls.Add(lblMT4);

            var txtMT4Path = new TextBox
            {
                Name = "txtMT4Path",
                Location = new Point(620, 26),
                Size = new Size(260, 18),
                Font = new Font("Segoe UI", 7F),
                Text = AutoDetectMT4Path()
            };
            headerPanel.Controls.Add(txtMT4Path);

            var btnBrowse = new Button
            {
                Name = "btnBrowse",
                Text = "📂",
                Location = new Point(885, 26),
                Size = new Size(25, 18),
                BackColor = Color.FromArgb(249, 115, 22),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 7F)
            };
            btnBrowse.Click += BtnBrowse_Click;
            headerPanel.Controls.Add(btnBrowse);

            CreateStatusPanel(headerPanel);
        }

        private void CreateStatusPanel(Panel parent)
        {
            var statusPanel = new Panel
            {
                Name = "statusPanel",
                Location = new Point(920, 3),
                Size = new Size(300, 60),
                BackColor = Color.FromArgb(249, 115, 22),
                BorderStyle = BorderStyle.FixedSingle
            };

            var lblConnectionStatus = new Label
            {
                Name = "lblConnectionStatus",
                Text = "✅ CONNECTED & AUTHORIZED",
                Location = new Point(8, 3),
                Size = new Size(284, 15),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            statusPanel.Controls.Add(lblConnectionStatus);

            var lblChannelsCount = new Label
            {
                Name = "lblChannelsCount",
                Text = "📢 Channels: 0",
                Location = new Point(8, 20),
                Size = new Size(90, 12),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 7F)
            };
            statusPanel.Controls.Add(lblChannelsCount);

            var lblSelectedCount = new Label
            {
                Name = "lblSelectedCount",
                Text = "✅ Selected: 0",
                Location = new Point(100, 20),
                Size = new Size(85, 12),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 7F)
            };
            statusPanel.Controls.Add(lblSelectedCount);

            var lblSignalsCount = new Label
            {
                Name = "lblSignalsCount",
                Text = "📊 Today: 0",
                Location = new Point(188, 20),
                Size = new Size(100, 12),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 7F)
            };
            statusPanel.Controls.Add(lblSignalsCount);

            var lblMonitoringStatus = new Label
            {
                Name = "lblMonitoringStatus",
                Text = "⏯️ Ready to monitor",
                Location = new Point(8, 35),
                Size = new Size(284, 20),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 7F),
                TextAlign = ContentAlignment.MiddleCenter
            };
            statusPanel.Controls.Add(lblMonitoringStatus);

            parent.Controls.Add(statusPanel);
        }

        private void CreateMainContent()
        {
            var mainContainer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(20, 15, 20, 10),
                BackColor = Color.FromArgb(245, 245, 245)
            };

            mainContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
            mainContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));
            mainContainer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var leftPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(5)
            };
            mainContainer.Controls.Add(leftPanel, 0, 0);

            var rightPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(248, 249, 250),
                Padding = new Padding(10),
                BorderStyle = BorderStyle.FixedSingle
            };
            mainContainer.Controls.Add(rightPanel, 1, 0);

            this.Controls.Add(mainContainer);

            CreateChannelsSection(leftPanel);
            CreateControlsSection(rightPanel);
        }

        private void CreateChannelsSection(Panel parent)
        {
            var lblAllChannels = new Label
            {
                Text = "📢 ALL YOUR TELEGRAM CHANNELS",
                Dock = DockStyle.Top,
                Height = 25,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                ForeColor = Color.FromArgb(37, 99, 235),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(5, 5, 0, 0)
            };
            parent.Controls.Add(lblAllChannels);

            var searchPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(5)
            };

            var txtSearch = new TextBox
            {
                Name = "txtSearch",
                Location = new Point(5, 8),
                Size = new Size(200, 25),
                Font = new Font("Segoe UI", 10F),
                PlaceholderText = "Search channels..."
            };
            txtSearch.TextChanged += TxtSearch_TextChanged;
            searchPanel.Controls.Add(txtSearch);

            var cmbFilter = new ComboBox
            {
                Name = "cmbFilter",
                Location = new Point(210, 8),
                Size = new Size(100, 25),
                Font = new Font("Segoe UI", 9F),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbFilter.Items.AddRange(new[] { "All Types", "VIP", "Premium", "Signals", "Gold", "Crypto", "Groups" });
            cmbFilter.SelectedIndex = 0;
            cmbFilter.SelectedIndexChanged += CmbFilter_SelectedIndexChanged;
            searchPanel.Controls.Add(cmbFilter);

            var btnRefresh = new Button
            {
                Name = "btnRefreshChannels",
                Text = "🔄",
                Location = new Point(315, 8),
                Size = new Size(30, 25),
                BackColor = Color.FromArgb(34, 197, 94),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnRefresh.Click += BtnRefreshChannels_Click;
            searchPanel.Controls.Add(btnRefresh);

            var btnFindIndicator = new Button
            {
                Text = "🔍 Find Indicator",
                Location = new Point(350, 8),
                Size = new Size(110, 25),
                BackColor = Color.FromArgb(59, 130, 246),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold)
            };
            btnFindIndicator.Click += (s, e) =>
            {
                txtSearch.Text = "indicator";
                SearchAndHighlightChannel("indicator");
            };
            searchPanel.Controls.Add(btnFindIndicator);

            parent.Controls.Add(searchPanel);

            var lvChannels = new ListView
            {
                Name = "lvChannels",
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                CheckBoxes = true,
                Font = new Font("Segoe UI", 9F)
            };

            lvChannels.Columns.Add("Channel Name", 280);
            lvChannels.Columns.Add("ID", 100);
            lvChannels.Columns.Add("Type", 80);
            lvChannels.Columns.Add("Members", 80);
            lvChannels.Columns.Add("Activity", 50);

            lvChannels.ItemChecked += LvChannels_ItemChecked;
            parent.Controls.Add(lvChannels);
        }

        private void CreateControlsSection(Panel parent)
        {
            var lblSelected = new Label
            {
                Text = "✅ SELECTED CHANNELS",
                Location = new Point(10, 10),
                Size = new Size(400, 25),
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(34, 197, 94),
                BackColor = Color.Transparent
            };
            parent.Controls.Add(lblSelected);

            var btnDashboard = new Button
            {
                Name = "btnDashboard",
                Text = "📊 DASHBOARD",
                Location = new Point(265, 330), // Adjust position as needed
                Size = new Size(110, 30),
                BackColor = Color.FromArgb(59, 130, 246),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold)
            };
            btnDashboard.Click += BtnDashboard_Click;
            parent.Controls.Add(btnDashboard);
            var lvSelected = new ListView
            {
                Name = "lvSelected",
                Location = new Point(10, 40),
                Size = new Size(parent.Width - 20, 150), // Use parent width
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            lvSelected.Columns.Add("Channel", 200);
            lvSelected.Columns.Add("ID", 100);
            lvSelected.Columns.Add("Signals", 60);
            lvSelected.Columns.Add("Status", 120);

            parent.Controls.Add(lvSelected);

            var lblControls = new Label
            {
                Text = "🎮 MONITORING CONTROLS",
                Location = new Point(10, 200),
                Size = new Size(400, 25),
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(37, 99, 235),
                BackColor = Color.Transparent
            };
            parent.Controls.Add(lblControls);

            // Monitoring buttons - adjusted sizes
            var btnStartMonitoring = new Button
            {
                Name = "btnStartMonitoring",
                Text = "▶️ START MONITORING",
                Location = new Point(10, 235),
                Size = new Size(200, 50), // Reduced width
                BackColor = Color.FromArgb(34, 197, 94),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                Enabled = true,
                UseVisualStyleBackColor = false
            };
            btnStartMonitoring.FlatAppearance.BorderSize = 0;
            btnStartMonitoring.Click += BtnStartMonitoring_Click;
            parent.Controls.Add(btnStartMonitoring);

            var btnStopMonitoring = new Button
            {
                Name = "btnStopMonitoring",
                Text = "⏹️ STOP MONITORING",
                Location = new Point(220, 235),
                Size = new Size(180, 50), // Reduced width
                BackColor = Color.FromArgb(220, 38, 38),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                Enabled = false,
                UseVisualStyleBackColor = false
            };
            btnStopMonitoring.FlatAppearance.BorderSize = 0;
            btnStopMonitoring.Click += BtnStopMonitoring_Click;
            parent.Controls.Add(btnStopMonitoring);

            // Utility buttons - TWO ROWS for better fit
            // First row
            var btnCopyChannelIDs = new Button
            {
                Name = "btnCopyChannelIDs",
                Text = "📋 COPY IDs",
                Location = new Point(10, 295),
                Size = new Size(85, 30), // Smaller buttons
                BackColor = Color.FromArgb(168, 85, 247),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                UseVisualStyleBackColor = false
            };
            btnCopyChannelIDs.FlatAppearance.BorderSize = 0;
            btnCopyChannelIDs.Click += BtnCopyChannelIDs_Click;
            parent.Controls.Add(btnCopyChannelIDs);

            var btnTestSignal = new Button
            {
                Name = "btnTestSignal",
                Text = "🧪 TEST",
                Location = new Point(100, 295),
                Size = new Size(60, 30),
                BackColor = Color.FromArgb(249, 115, 22),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                UseVisualStyleBackColor = false
            };
            btnTestSignal.FlatAppearance.BorderSize = 0;
            btnTestSignal.Click += BtnTestSignal_Click;
            parent.Controls.Add(btnTestSignal);

            var btnGenerateEAConfig = new Button
            {
                Name = "btnGenerateEAConfig",
                Text = "⚙️ CONFIG",
                Location = new Point(165, 295),
                Size = new Size(75, 30),
                BackColor = Color.FromArgb(59, 130, 246),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                UseVisualStyleBackColor = false
            };
            btnGenerateEAConfig.FlatAppearance.BorderSize = 0;
            btnGenerateEAConfig.Click += BtnGenerateEAConfig_Click;
            parent.Controls.Add(btnGenerateEAConfig);

            var btnClearOldSignals = new Button
            {
                Name = "btnClearOldSignals",
                Text = "🧹 CLEAR",
                Location = new Point(245, 295),
                Size = new Size(70, 30),
                BackColor = Color.FromArgb(107, 114, 128),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                UseVisualStyleBackColor = false
            };
            btnClearOldSignals.FlatAppearance.BorderSize = 0;
            btnClearOldSignals.Click += (s, e) =>
            {
                ClearOldSignalsFromFile();
                MessageBox.Show("✅ Old signals cleared from file!\n\nOnly signals from the last hour are kept.",
                               "File Cleaned", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            parent.Controls.Add(btnClearOldSignals);

            // Second row
            var btnCheckFile = new Button
            {
                Name = "btnCheckFile",
                Text = "📂 CHECK",
                Location = new Point(320, 295),
                Size = new Size(70, 30),
                BackColor = Color.FromArgb(75, 85, 99),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                UseVisualStyleBackColor = false
            };
            btnCheckFile.FlatAppearance.BorderSize = 0;
            btnCheckFile.Click += (s, e) => CheckSignalFile();
            parent.Controls.Add(btnCheckFile);

            var btnDebug = new Button
            {
                Name = "btnDebug",
                Text = isDebugFormOpen ? "🐛 HIDE DEBUG" : "🐛 SHOW DEBUG",
                Location = new Point(10, 330), // Second row
                Size = new Size(115, 30),
                BackColor = isDebugFormOpen ? Color.FromArgb(220, 38, 38) : Color.FromArgb(239, 68, 68),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                UseVisualStyleBackColor = false
            };
            btnDebug.FlatAppearance.BorderSize = 0;
            btnDebug.Click += (s, e) => {
                try
                {
                    if (isDebugFormOpen && debugForm != null && !debugForm.IsDisposed)
                    {
                        debugForm.Close();
                        btnDebug.Text = "🐛 SHOW DEBUG";
                        btnDebug.BackColor = Color.FromArgb(239, 68, 68);
                        LogMessage("Debug console closed");
                    }
                    else
                    {
                        CreateDebugConsole();
                        btnDebug.Text = "🐛 HIDE DEBUG";
                        btnDebug.BackColor = Color.FromArgb(220, 38, 38);
                        LogMessage("Debug console opened");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ Debug button error: {ex.Message}");
                    MessageBox.Show($"❌ Debug console error:\n\n{ex.Message}",
                                   "Debug Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            parent.Controls.Add(btnDebug);

            var btnPerformance = new Button
            {
                Text = "📊 PERF",
                Location = new Point(130, 330), // Second row
                Size = new Size(65, 30),
                BackColor = Color.FromArgb(147, 51, 234),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold)
            };
            btnPerformance.Click += (s, e) => ShowPerformanceReport();
            parent.Controls.Add(btnPerformance);

            // Add Save History button
            var btnSaveHistory = new Button
            {
                Text = "💾 SAVE",
                Location = new Point(200, 330), // Second row
                Size = new Size(60, 30),
                BackColor = Color.FromArgb(34, 197, 94),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold)
            };
            btnSaveHistory.Click += (s, e) => {
                signalProcessor?.ForceSaveHistory();
                ShowMessage("✅ Signal history saved successfully!", "History Saved", MessageBoxIcon.Information);
            };
            parent.Controls.Add(btnSaveHistory);

            var lblLiveSignals = new Label
            {
                Text = "📊 LIVE SIGNALS FEED",
                Location = new Point(10, 370),
                Size = new Size(400, 25),
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(249, 115, 22),
                BackColor = Color.Transparent
            };
            parent.Controls.Add(lblLiveSignals);

            var lvLiveSignals = new ListView
            {
                Name = "lvLiveSignals",
                Location = new Point(10, 400),
                Size = new Size(520, 300), // Use parent width and adjust height
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Segoe UI", 8F),
                BackColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };

            lvLiveSignals.Columns.Add("Time", 60);
            lvLiveSignals.Columns.Add("Channel", 100);
            lvLiveSignals.Columns.Add("Symbol", 60);
            lvLiveSignals.Columns.Add("Type", 70); // NEW: Order type column
            lvLiveSignals.Columns.Add("SL", 50);
            lvLiveSignals.Columns.Add("TP", 50);
            lvLiveSignals.Columns.Add("Status", 90);

            parent.Controls.Add(lvLiveSignals);
        }


        private void CreateBottomPanel()
        {
            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 80,
                BackColor = Color.FromArgb(37, 99, 235)
            };
            this.Controls.Add(bottomPanel);

            var lblUser = new Label
            {
                Name = "lblUser",
                Text = "👤 islamahmed9717",
                Location = new Point(20, 25),
                Size = new Size(200, 25),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold)
            };
            bottomPanel.Controls.Add(lblUser);

            CreateBottomButtons(bottomPanel);

            var lblStats = new Label
            {
                Name = "lblStats",
                Text = "📊 System ready - Connect to Telegram to start",
                Location = new Point(800, 25),
                Size = new Size(500, 25),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10F),
                TextAlign = ContentAlignment.MiddleRight
            };
            bottomPanel.Controls.Add(lblStats);
        }

        private void CreateBottomButtons(Panel parent)
        {
            var btnHistory = new Button
            {
                Text = "📈 SIGNALS HISTORY",
                Location = new Point(250, 20),
                Size = new Size(160, 40),
                BackColor = Color.FromArgb(34, 197, 94),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };
            btnHistory.Click += BtnHistory_Click;
            parent.Controls.Add(btnHistory);

            var btnEASettings = new Button
            {
                Text = "⚙️ EA SETTINGS",
                Location = new Point(420, 20),
                Size = new Size(130, 40),
                BackColor = Color.FromArgb(249, 115, 22),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };
            btnEASettings.Click += BtnEASettings_Click;
            parent.Controls.Add(btnEASettings);

            var btnSymbolMapping = new Button
            {
                Text = "🗺️ SYMBOL MAPPING",
                Location = new Point(560, 20),
                Size = new Size(170, 40),
                BackColor = Color.FromArgb(168, 85, 247),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };
            btnSymbolMapping.Click += BtnSymbolMapping_Click;
            parent.Controls.Add(btnSymbolMapping);
        }

        private void CreateStatusBar()
        {
            var statusStrip = new StatusStrip
            {
                BackColor = Color.FromArgb(250, 250, 250)
            };

            var statusLabel = new ToolStripStatusLabel
            {
                Name = "statusLabel",
                Text = $"Ready - Current UTC Now: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}",
                Font = new Font("Segoe UI", 9F),
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft
            };

            var versionLabel = new ToolStripStatusLabel
            {
                Name = "versionLabel",
                Text = "v2.0.0 - Real Implementation",
                Font = new Font("Segoe UI", 9F)
            };

            statusStrip.Items.Add(statusLabel);
            statusStrip.Items.Add(versionLabel);
            this.Controls.Add(statusStrip);
        }
        #endregion

        #region Event Handlers
        private async void TelegramService_NewMessageReceived(object? sender, (string message, long channelId, string channelName, DateTime messageTime) e)
        {
            try
            {
                perfMonitor.StartTimer("ProcessMessage");

                // Process in background to avoid UI blocking
                var processedSignal = await Task.Run(() =>
                    signalProcessor.ProcessTelegramMessage(e.message, e.channelId, e.channelName)
                );

                // Update UI on UI thread
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke(new Action(() => {
                        if (!processedSignal.Status.Contains("Duplicate"))
                        {
                            UpdateUIAfterSignal(processedSignal, e.channelId, e.channelName);
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Error processing message: {ex.Message}");
            }
            finally
            {
                perfMonitor.EndTimer("ProcessMessage");
            }
        }

        private void UpdateUIAfterSignal(ProcessedSignal processedSignal, long channelId, string channelName)
        {
            try
            {
                if (!processedSignal.Status.Contains("Duplicate"))
                {
                    lock (allSignals)
                    {
                        allSignals.Add(processedSignal);
                        if (allSignals.Count > 1000)
                        {
                            allSignals.RemoveRange(0, allSignals.Count - 1000);
                        }
                    }

                    AddToLiveSignals(processedSignal);
                    UpdateSelectedChannelSignalCount(channelId);
                    UpdateSignalsCount();

                    LogMessage($"📨 New signal from {channelName}: {processedSignal.ParsedData?.Symbol} {processedSignal.ParsedData?.Direction} - {processedSignal.Status}");

                    if (processedSignal.Status.Contains("Processed"))
                    {
                        ShowNotification($"📊 New Signal: {processedSignal.ParsedData?.Symbol} {processedSignal.ParsedData?.Direction}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ UI update error: {ex.Message}");
            }
        }

        private void AddToLiveSignals(ProcessedSignal signal)
        {
            if (signal.Status.Contains("Duplicate"))
                return;

            lock (liveFeedLock)
            {
                pendingSignals.Enqueue(signal);
            }

            // Batch update UI every 250ms instead of immediate updates
            if (liveFeedUpdateTimer == null)
            {
                liveFeedUpdateTimer = new System.Windows.Forms.Timer { Interval = 250 };
                liveFeedUpdateTimer.Tick += LiveFeedUpdateTimer_Tick;
                liveFeedUpdateTimer.Start();
            }
        }

        private void LiveFeedUpdateTimer_Tick(object? sender, EventArgs e)
        {
            var lvLiveSignals = this.Controls.Find("lvLiveSignals", true).FirstOrDefault() as ListView;
            if (lvLiveSignals == null) return;

            List<ProcessedSignal> signalsToAdd;
            lock (liveFeedLock)
            {
                if (pendingSignals.Count == 0) return;
                signalsToAdd = new List<ProcessedSignal>(pendingSignals);
                pendingSignals.Clear();
            }

            lvLiveSignals.BeginUpdate();
            try
            {
                foreach (var signal in signalsToAdd)
                {
                    var localTime = signal.DateTime.ToLocalTime();
                    var item = new ListViewItem(localTime.ToString("HH:mm:ss"));
                    item.SubItems.Add(signal.ChannelName);
                    item.SubItems.Add(signal.ParsedData?.Symbol ?? "N/A");
                    item.SubItems.Add(signal.ParsedData?.Direction ?? "N/A");
                    item.SubItems.Add(signal.ParsedData?.StopLoss > 0 ? signal.ParsedData.StopLoss.ToString("F5") : "N/A");
                    item.SubItems.Add(signal.ParsedData?.TakeProfit1 > 0 ? signal.ParsedData.TakeProfit1.ToString("F5") : "N/A");
                    item.SubItems.Add(signal.Status);

                    // Color coding
                    if (signal.Status.Contains("Processed"))
                        item.BackColor = Color.FromArgb(220, 255, 220);
                    else if (signal.Status.Contains("Error") || signal.Status.Contains("Invalid"))
                        item.BackColor = Color.FromArgb(255, 220, 220);
                    else if (signal.Status.Contains("Ignored"))
                        item.BackColor = Color.FromArgb(255, 255, 220);
                    else if (signal.Status.Contains("Test"))
                        item.BackColor = Color.FromArgb(220, 220, 255);

                    lvLiveSignals.Items.Insert(0, item);
                }

                // Keep only last 100 signals
                while (lvLiveSignals.Items.Count > 100)
                {
                    lvLiveSignals.Items.RemoveAt(lvLiveSignals.Items.Count - 1);
                }
            }
            finally
            {
                lvLiveSignals.EndUpdate();
            }
        }

        private async void BtnConnect_Click(object? sender, EventArgs e)
        {
            perfMonitor.StartTimer("Connect");
            try
            {
                var cmbPhone = this.Controls.Find("cmbPhone", true)[0] as ComboBox;
                phoneNumber = cmbPhone?.Text?.Trim() ?? "";

                bool isAlreadyConnected = telegramService.IsUserAuthorized();

                if (isAlreadyConnected && !string.IsNullOrEmpty(phoneNumber))
                {
                    var result = MessageBox.Show("✅ Already connected to Telegram!\n\n🔄 Do you want to reload channels?\n\n" +
                                               "Click YES to refresh channel list\n" +
                                               "Click NO to reconnect with different account",
                                               "Already Connected",
                                               MessageBoxButtons.YesNoCancel,
                                               MessageBoxIcon.Question);

                    if (result == DialogResult.Yes)
                    {
                        await ReloadChannels();
                        return;
                    }
                    else if (result == DialogResult.Cancel)
                    {
                        return;
                    }
                }

                if (string.IsNullOrEmpty(phoneNumber))
                {
                    ShowMessage("❌ Please enter your phone number", "Phone Required", MessageBoxIcon.Warning);
                    return;
                }

                if (!IsValidPhoneNumber(phoneNumber))
                {
                    ShowMessage("❌ Please enter a valid phone number with country code\nExample: +1234567890",
                               "Invalid Phone Number", MessageBoxIcon.Warning);
                    return;
                }

                var btnConnect = sender as Button;
                var originalText = btnConnect?.Text ?? "";
                if (btnConnect != null)
                {
                    btnConnect.Text = "🔄 CONNECTING...";
                    btnConnect.Enabled = false;
                }

                try
                {
                    bool connected = await ConnectWithRetry(phoneNumber);

                    if (connected)
                    {
                        await LoadChannelsAfterAuth(phoneNumber);
                    }
                    else
                    {
                        throw new Exception("Authentication failed or was cancelled");
                    }
                }
                catch (Exception ex)
                {
                    ShowMessage($"❌ Connection failed:\n\n{ex.Message}",
                               "Connection Error", MessageBoxIcon.Error);
                    UpdateStatus(false, false);
                }
                finally
                {
                    if (btnConnect != null)
                    {
                        btnConnect.Text = originalText;
                        btnConnect.Enabled = true;
                    }
                }
            }
            finally
            {
                perfMonitor.EndTimer("Connect");
            }
        }

        private async Task<bool> ConnectWithRetry(string phoneNumber, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    LogMessage($"🔄 Connection attempt {i + 1} of {maxRetries}...");

                    bool connected = await telegramService.ConnectAsync(phoneNumber);
                    if (connected)
                    {
                        LogMessage("✅ Connected successfully!");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ Attempt {i + 1} failed: {ex.Message}");

                    if (i < maxRetries - 1)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2 * (i + 1))); // Exponential backoff
                    }
                }
            }

            return false;
        }

        private async Task LoadChannelsAfterAuth(string phoneNumber)
        {
            perfMonitor.StartTimer("LoadChannels");
            try
            {
                UpdateStatus(true, true);

                var channels = await GetChannelsOptimized();

                UpdateChannelsList(channels);
                SavePhoneNumber(phoneNumber);

                LogMessage($"✅ Connected successfully - Phone: {phoneNumber}, Channels: {channels.Count}");

                ShowMessage($"✅ Successfully connected to Telegram!\n\n" +
                           $"📱 Phone: {phoneNumber}\n" +
                           $"📢 Found {channels.Count} channels\n" +
                           $"🎯 Select channels and start monitoring!",
                           "Connection Successful", MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowMessage($"❌ Failed to load channels:\n\n{ex.Message}", "Channel Loading Error", MessageBoxIcon.Error);
            }
            finally
            {
                perfMonitor.EndTimer("LoadChannels");
            }
        }

        private async Task<List<ChannelInfo>> GetChannelsOptimized(bool forceRefresh = false)
        {
            var cacheKey = phoneNumber ?? "default";

            // Check cache validity (5 minutes)
            if (!forceRefresh &&
                channelCache.ContainsKey(cacheKey) &&
                (DateTime.Now - lastChannelLoadTime).TotalMinutes < 5)
            {
                LogMessage("📊 Using cached channel list");
                return channelCache[cacheKey];
            }

            LogMessage("🔄 Loading channels from Telegram...");
            var channels = await telegramService.GetChannelsAsync();

            // Update cache
            channelCache[cacheKey] = channels;
            lastChannelLoadTime = DateTime.Now;

            return channels;
        }

        private async void BtnStartMonitoring_Click(object? sender, EventArgs e)
        {
            if (selectedChannels.Count == 0)
            {
                ShowMessage("⚠️ Please select at least one channel to monitor!",
                           "No Channels Selected", MessageBoxIcon.Warning);
                return;
            }

            var txtMT4Path = this.Controls.Find("txtMT4Path", true)[0] as TextBox;
            var mt4Path = txtMT4Path?.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(mt4Path) || !Directory.Exists(mt4Path))
            {
                ShowMessage("❌ Please set a valid MT4/MT5 Files folder path!",
                           "Invalid Path", MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Update UI
                var btnStart = sender as Button;
                var btnStop = this.Controls.Find("btnStopMonitoring", true)[0] as Button;
                if (btnStart != null) btnStart.Enabled = false;
                if (btnStop != null) btnStop.Enabled = true;

                // Update EA settings
                var currentSettings = signalProcessor.GetEASettings();
                currentSettings.MT4FilesPath = mt4Path;
                currentSettings.SignalFilePath = "telegram_signals.txt";
                signalProcessor.UpdateEASettings(currentSettings);

                // Start ENHANCED monitoring
                await telegramService.StartEnhancedMonitoring(selectedChannels); // Fix: Added 'await' here

                isMonitoring = true;

                // Show monitoring dashboard automatically
                ShowMonitoringDashboard();

                ShowMessage($"✅ Enhanced monitoring started!\n\n" +
                           $"📊 Monitoring {selectedChannels.Count} channels\n" +
                           $"📁 Signals saved to: {mt4Path}\\telegram_signals.txt\n" +
                           $"🔄 Adaptive polling active\n" +
                           $"📈 Dashboard opened for real-time stats",
                           "Monitoring Started", MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowMessage($"❌ Failed to start monitoring:\n\n{ex.Message}",
                           "Monitoring Error", MessageBoxIcon.Error);

                // Reset UI on error
                var btnStart = sender as Button;
                var btnStop = this.Controls.Find("btnStopMonitoring", true)[0] as Button;
                if (btnStart != null) btnStart.Enabled = true;
                if (btnStop != null) btnStop.Enabled = false;
            }
        }

        private async void BtnStopMonitoring_Click(object? sender, EventArgs e)
        {
            try
            {
                // Stop enhanced monitoring
                await telegramService.StopMonitoring();

                isMonitoring = false;

                // Close monitoring dashboard if open
                if (monitoringDashboard != null && !monitoringDashboard.IsDisposed)
                {
                    monitoringDashboard.Close();
                    monitoringDashboard = null;
                }

                // Update UI
                var btnStart = this.Controls.Find("btnStartMonitoring", true)[0] as Button;
                var btnStop = sender as Button;
                if (btnStart != null) btnStart.Enabled = true;
                if (btnStop != null) btnStop.Enabled = false;

                UpdateStatus(telegramService.IsUserAuthorized(), false);
                UpdateSelectedChannelsStatus("✅ Ready");

                ShowMessage("⏹️ Enhanced monitoring stopped successfully!",
                           "Monitoring Stopped", MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowMessage($"❌ Error stopping monitoring:\n\n{ex.Message}",
                           "Error", MessageBoxIcon.Information);
            }
        }

        private void BtnCopyChannelIDs_Click(object? sender, EventArgs e)
        {
            if (selectedChannels.Count == 0)
            {
                ShowMessage("⚠️ Please select channels first!", "No Channels Selected", MessageBoxIcon.Warning);
                return;
            }

            var channelIds = string.Join(",", selectedChannels.Select(c => c.Id.ToString()));
            try
            {
                Clipboard.SetText(channelIds);
            }
            catch (ExternalException)
            {
                ShowMessage("⚠️ Unable to copy to clipboard. Please try again.", "Clipboard Error", MessageBoxIcon.Error);
            }

            var channelList = string.Join("\n", selectedChannels.Select(c => $"• {c.Title} ({c.Id}) - {c.Type}"));

            ShowMessage($"📋 Channel IDs copied to clipboard!\n\n📝 PASTE THIS IN YOUR EA:\n{channelIds}\n\n📢 SELECTED CHANNELS:\n{channelList}",
                       "Channel IDs Copied", MessageBoxIcon.Information);
        }

        private void BtnTestSignal_Click(object? sender, EventArgs e)
        {
            var txtMT4Path = this.Controls.Find("txtMT4Path", true)[0] as TextBox;
            var mt4Path = txtMT4Path?.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(mt4Path) || !Directory.Exists(mt4Path))
            {
                ShowMessage("❌ Please set a valid MT4/MT5 path first!", "Invalid Path", MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Update EA settings with current MT4 path
                var currentSettings = signalProcessor.GetEASettings();
                currentSettings.MT4FilesPath = mt4Path;
                currentSettings.SignalFilePath = "telegram_signals.txt";
                signalProcessor.UpdateEASettings(currentSettings);

                // Create different test signals each time
                var testScenarios = new[]
                {
                    @"🚀 FOREX SIGNAL 🚀
BUY EURUSD @ 1.0890
SL: 1.0860
TP1: 1.0920
TP2: 1.0950
TP3: 1.0980",

                    @"📊 TRADING ALERT 📊
SELL GBPUSD NOW
Stop Loss: 1.2650
Take Profit 1: 1.2600
Take Profit 2: 1.2550",

                    @"🏆 GOLD SIGNAL 🏆
BUY GOLD (XAUUSD)
Entry: Market Price
SL: 1945.00
TP: 1965.00",

                    @"💹 SIGNAL TIME 💹
USDJPY BUY NOW
SL 148.50
TP 149.50"
                };

                // Rotate through different test signals
                var random = new Random();
                var testMessage = testScenarios[random.Next(testScenarios.Length)];

                // Process the test message with current timestamp
                var processedSignal = signalProcessor.ProcessTelegramMessage(
                    testMessage,
                    999999,
                    "TEST_CHANNEL"
                );

                // Add to signals history and UI
                allSignals.Add(processedSignal);
                AddToLiveSignals(processedSignal);

                // Verify file was written
                var signalFilePath = Path.Combine(mt4Path, "telegram_signals.txt");
                var fileWritten = File.Exists(signalFilePath);
                var fileSize = fileWritten ? new FileInfo(signalFilePath).Length : 0;

                // Read last line from file to verify
                string lastLine = "";
                if (fileWritten)
                {
                    var lines = File.ReadAllLines(signalFilePath);
                    lastLine = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#")) ?? "";
                }

                ShowMessage($"🧪 TEST SIGNAL RESULTS:\n\n" +
                            $"✅ Signal Type: {processedSignal.ParsedData?.Symbol} {processedSignal.ParsedData?.Direction}\n" +
                            $"📝 Status: {processedSignal.Status}\n" +
                            $"📁 File Written: {(fileWritten ? "YES ✅" : "NO ❌")}\n" +
                            $"📏 File Size: {fileSize} bytes\n" +
                            $"🕒 Timestamp: {DateTime.Now:yyyy.MM.dd HH:mm:ss}\n\n" +
                            $"📄 Last Line in File:\n{lastLine}\n\n" +
                            $"💡 Now check your EA - it should process this signal immediately!",
                            "Test Signal Complete",
                            MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowMessage($"❌ Failed to process test signal:\n\n{ex.Message}", "Test Failed", MessageBoxIcon.Error);
            }
        }

        private void BtnGenerateEAConfig_Click(object? sender, EventArgs e)
        {
            if (selectedChannels.Count == 0)
            {
                ShowMessage("⚠️ Please select channels first!", "No Channels Selected", MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var config = GenerateEAConfiguration();

                var saveDialog = new SaveFileDialog
                {
                    Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                    FileName = $"TelegramEA_Config_islamahmed9717_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    Title = "Save EA Configuration"
                };

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    File.WriteAllText(saveDialog.FileName, config);
                    Clipboard.SetText(config);

                    ShowMessage($"⚙️ EA configuration generated successfully!\n\n📁 Saved to: {saveDialog.FileName}\n📋 Configuration also copied to clipboard!",
                               "Configuration Generated", MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"❌ Failed to generate configuration:\n\n{ex.Message}", "Generation Error", MessageBoxIcon.Error);
            }
        }

        private void BtnBrowse_Click(object? sender, EventArgs e)
        {
            using (var folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "Select your MT4/MT5 Files folder (usually contains MQL4 or MQL5 subfolder)";
                folderDialog.ShowNewFolderButton = false;

                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    var txtMT4Path = this.Controls.Find("txtMT4Path", true)[0] as TextBox;
                    if (txtMT4Path != null)
                    {
                        txtMT4Path.Text = folderDialog.SelectedPath;
                        SaveMT4Path(folderDialog.SelectedPath);
                    }
                }
            }
        }

        private async void BtnRefreshChannels_Click(object? sender, EventArgs e)
        {
            if (!telegramService.IsUserAuthorized())
            {
                ShowMessage("❌ Please connect to Telegram first!", "Not Connected", MessageBoxIcon.Warning);
                return;
            }

            await ReloadChannels();
        }

        private void BtnHistory_Click(object? sender, EventArgs e)
        {
            var historyForm = new SignalsHistoryForm(allSignals);
            historyForm.ShowDialog();
        }

        private void BtnEASettings_Click(object? sender, EventArgs e)
        {
            var currentSettings = signalProcessor.GetEASettings();
            using (var settingsForm = new EASettingsForm(currentSettings))
            {
                if (settingsForm.ShowDialog() == DialogResult.OK)
                {
                    var updatedSettings = settingsForm.GetUpdatedSettings();
                    signalProcessor.UpdateEASettings(updatedSettings);
                    ShowMessage("✅ EA settings updated successfully!", "Settings Saved", MessageBoxIcon.Information);
                }
            }
        }

        private void BtnSymbolMapping_Click(object? sender, EventArgs e)
        {
            var currentMapping = signalProcessor.GetSymbolMapping();
            using (var mappingForm = new SymbolMappingForm(currentMapping))
            {
                if (mappingForm.ShowDialog() == DialogResult.OK)
                {
                    var updatedMapping = mappingForm.GetUpdatedMapping();
                    signalProcessor.UpdateSymbolMapping(updatedMapping);
                    ShowMessage("✅ Symbol mapping updated successfully!", "Mapping Saved", MessageBoxIcon.Information);
                }
            }
        }

        private void TxtSearch_TextChanged(object? sender, EventArgs e)
        {
            var txtSearch = sender as TextBox;
            if (txtSearch == null) return;

            string searchText = txtSearch.Text.Trim();

            if (!string.IsNullOrEmpty(searchText))
            {
                SearchAndHighlightChannel(searchText);
            }
            else
            {
                // Clear highlighting when search is empty
                var lvChannels = this.Controls.Find("lvChannels", true)[0] as ListView;
                if (lvChannels != null)
                {
                    foreach (ListViewItem item in lvChannels.Items)
                    {
                        var channel = item.Tag as ChannelInfo;
                        if (channel != null)
                        {
                            RestoreChannelItemColor(item, channel);
                            item.Font = new Font("Segoe UI", 9F);
                        }
                    }
                }
            }

            ApplyChannelFilters();
        }

        private void CmbFilter_SelectedIndexChanged(object? sender, EventArgs e)
        {
            ApplyChannelFilters();
        }

        private void LvChannels_ItemChecked(object? sender, ItemCheckedEventArgs e)
        {
            var channel = e.Item.Tag as ChannelInfo;
            if (channel == null) return;

            var lvSelected = this.Controls.Find("lvSelected", true).FirstOrDefault() as ListView;
            if (lvSelected == null) return;

            if (e.Item.Checked)
            {
                // Add to selected channels
                if (!selectedChannels.Any(c => c.Id == channel.Id))
                {
                    selectedChannels.Add(channel);

                    var item = new ListViewItem(channel.Title);
                    item.SubItems.Add(channel.Id.ToString());
                    item.SubItems.Add("0"); // Signals count
                    item.SubItems.Add("✅ Ready"); // Status
                    item.Tag = channel;
                    item.BackColor = Color.FromArgb(255, 255, 220); // Light yellow initially

                    lvSelected.Items.Add(item);
                }
            }
            else
            {
                // Remove from selected channels
                selectedChannels.RemoveAll(c => c.Id == channel.Id);

                for (int i = lvSelected.Items.Count - 1; i >= 0; i--)
                {
                    var selectedChannel = lvSelected.Items[i].Tag as ChannelInfo;
                    if (selectedChannel?.Id == channel.Id)
                    {
                        lvSelected.Items.RemoveAt(i);
                        break;
                    }
                }
            }

            UpdateSelectedCount();
            lvSelected.Refresh();
        }
        private void BtnDashboard_Click(object sender, EventArgs e)
        {
            ShowMonitoringDashboard();
        }

        private void ShowMonitoringDashboard()
        {
            if (monitoringDashboard == null || monitoringDashboard.IsDisposed)
            {
                monitoringDashboard = new MonitoringDashboard(telegramService, signalProcessor);
                monitoringDashboard.FormClosed += (s, e) => monitoringDashboard = null;
            }

            monitoringDashboard.Show();
            monitoringDashboard.BringToFront();
        }
        private void UiUpdateTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                // Throttle updates to prevent excessive CPU usage
                if ((DateTime.Now - lastUIUpdate).TotalMilliseconds < 500)
                    return;

                lastUIUpdate = DateTime.Now;

                // Update only visible components
                if (this.WindowState != FormWindowState.Minimized)
                {
                    UpdateTimeDisplays();
                    UpdateSignalsCount();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Timer error: {ex.Message}");
            }
        }
        #endregion

        #region Event Handler Helpers
        private void TelegramService_ErrorOccurred(object? sender, string e)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => LogMessage($"🔴 Telegram Error: {e}")));
            }
            else
            {
                LogMessage($"🔴 Telegram Error: {e}");
            }
        }

        private void TelegramService_DebugMessage(object? sender, string message)
        {
            LogDebugMessage($"📡 TELEGRAM: {message}");
            Console.WriteLine($"[TELEGRAM] {message}");
        }

        private void SignalProcessor_SignalProcessed(object? sender, ProcessedSignal e)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => {
                    LogMessage($"✅ Signal processed: {e.ParsedData?.Symbol} {e.ParsedData?.Direction} - {e.Status}");
                    ShowNotification($"📊 New Signal: {e.ParsedData?.Symbol} {e.ParsedData?.Direction}");
                }));
            }
            else
            {
                LogMessage($"✅ Signal processed: {e.ParsedData?.Symbol} {e.ParsedData?.Direction} - {e.Status}");
                ShowNotification($"📊 New Signal: {e.ParsedData?.Symbol} {e.ParsedData?.Direction}");
            }
        }

        private void SignalProcessor_ErrorOccurred(object? sender, string e)
        {
            LogDebugMessage($"[ERROR] {e}");
            LogMessage($"🔴 Signal Processing Error: {e}");
        }

        private void SignalProcessor_DebugMessage(object? sender, string message)
        {
            LogDebugMessage($"[PROCESSOR] {message}");
            Console.WriteLine($"[PROCESSOR] {message}");
        }
        #endregion

        #region Helper Methods
        private void ShowNotification(string message)
        {
            try
            {
                var notificationForm = new Form
                {
                    Size = new Size(300, 100),
                    StartPosition = FormStartPosition.Manual,
                    FormBorderStyle = FormBorderStyle.None,
                    BackColor = Color.FromArgb(34, 197, 94),
                    TopMost = true,
                    ShowInTaskbar = false
                };

                var lblMessage = new Label
                {
                    Text = message,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 10F, FontStyle.Bold)
                };
                notificationForm.Controls.Add(lblMessage);

                notificationForm.Location = new Point(
                    Screen.PrimaryScreen.WorkingArea.Right - notificationForm.Width - 10,
                    Screen.PrimaryScreen.WorkingArea.Bottom - notificationForm.Height - 10
                );

                notificationForm.Show();

                var timer = new System.Windows.Forms.Timer { Interval = 3000 };
                timer.Tick += (s, e) => {
                    timer.Stop();
                    notificationForm.Close();
                };
                timer.Start();
            }
            catch
            {
                // Ignore notification errors
            }
        }

        private void UpdateStatus(bool isConnected, bool isAuthorized)
        {
            var statusPanel = this.Controls.Find("statusPanel", true)[0] as Panel;
            var lblConnectionStatus = statusPanel?.Controls.Find("lblConnectionStatus", true)[0] as Label;
            var lblMonitoringStatus = statusPanel?.Controls.Find("lblMonitoringStatus", true)[0] as Label;

            if (lblConnectionStatus != null && lblMonitoringStatus != null && statusPanel != null)
            {
                if (isMonitoring)
                {
                    statusPanel.BackColor = Color.FromArgb(34, 197, 94); // Green
                    lblConnectionStatus.Text = "✅ LIVE MONITORING";
                    lblMonitoringStatus.Text = $"📊 Active on {selectedChannels.Count} channels";
                }
                else if (isConnected && isAuthorized)
                {
                    statusPanel.BackColor = Color.FromArgb(249, 115, 22); // Orange
                    lblConnectionStatus.Text = "🔗 CONNECTED";
                    lblMonitoringStatus.Text = "⏯️ Ready to monitor";
                }
                else
                {
                    statusPanel.BackColor = Color.FromArgb(220, 38, 38); // Red
                    lblConnectionStatus.Text = "❌ DISCONNECTED";
                    lblMonitoringStatus.Text = "⏸️ Not connected";
                }
            }
        }

        private void UpdateSelectedChannelSignalCount(long channelId)
        {
            var lvSelected = this.Controls.Find("lvSelected", true).FirstOrDefault() as ListView;
            if (lvSelected == null) return;

            if (lvSelected.InvokeRequired)
            {
                lvSelected.Invoke(new Action(() => UpdateChannelCountInternal(lvSelected, channelId)));
            }
            else
            {
                UpdateChannelCountInternal(lvSelected, channelId);
            }
        }

        private void UpdateChannelCountInternal(ListView lvSelected, long channelId)
        {
            foreach (ListViewItem item in lvSelected.Items)
            {
                var channel = item.Tag as ChannelInfo;
                if (channel != null && channel.Id == channelId)
                {
                    var signalsFromChannel = allSignals.Count(s => s.ChannelId == channelId);

                    if (item.SubItems.Count > 2)
                    {
                        item.SubItems[2].Text = signalsFromChannel.ToString();
                    }

                    item.BackColor = Color.FromArgb(200, 255, 200); // Light green flash

                    var timer = new System.Windows.Forms.Timer { Interval = 1000 };
                    timer.Tick += (s, e) => {
                        item.BackColor = Color.FromArgb(220, 255, 220);
                        timer.Stop();
                        timer.Dispose();
                    };
                    timer.Start();

                    break;
                }
            }
        }

        private void UpdateSelectedChannelsStatus(string status)
        {
            var lvSelected = this.Controls.Find("lvSelected", true).FirstOrDefault() as ListView;
            if (lvSelected == null) return;

            if (lvSelected.InvokeRequired)
            {
                lvSelected.Invoke(new Action(() => UpdateSelectedChannelsStatus(status)));
                return;
            }

            foreach (ListViewItem item in lvSelected.Items)
            {
                if (item.SubItems.Count > 3)
                {
                    item.SubItems[3].Text = status;
                }

                if (status.Contains("Live") || status.Contains("📊"))
                {
                    item.BackColor = Color.FromArgb(200, 255, 200);
                    item.ForeColor = Color.Black;
                }
                else if (status.Contains("Ready") || status.Contains("✅"))
                {
                    item.BackColor = Color.FromArgb(255, 255, 220);
                    item.ForeColor = Color.Black;
                }
                else
                {
                    item.BackColor = Color.White;
                    item.ForeColor = Color.Black;
                }
            }

            lvSelected.Refresh();
        }

        private void UpdateChannelsCount()
        {
            var lblChannelsCount = this.Controls.Find("lblChannelsCount", true)[0] as Label;
            if (lblChannelsCount != null)
            {
                lblChannelsCount.Text = $"📢 Channels: {allChannels.Count}";
            }
        }

        private void UpdateSelectedCount()
        {
            var lblSelectedCount = this.Controls.Find("lblSelectedCount", true)[0] as Label;
            if (lblSelectedCount != null)
            {
                lblSelectedCount.Text = $"✅ Selected: {selectedChannels.Count}";
            }

            var btnStartMonitoring = this.Controls.Find("btnStartMonitoring", true)[0] as Button;
            if (btnStartMonitoring != null)
            {
                btnStartMonitoring.Enabled = selectedChannels.Count > 0 && !isMonitoring;
            }
        }

        private void UpdateSignalsCount()
        {
            var todaySignals = allSignals.Count(s => s.DateTime.Date == DateTime.Now.Date);

            var lblSignalsCount = this.Controls.Find("lblSignalsCount", true)[0] as Label;
            if (lblSignalsCount != null)
            {
                lblSignalsCount.Text = $"📊 Today: {todaySignals}";
            }

            var lblStats = this.Controls.Find("lblStats", true)[0] as Label;
            if (lblStats != null)
            {
                lblStats.Text = $"📊 Live System | Today: {todaySignals} signals | Total: {allSignals.Count} | Monitoring: {selectedChannels.Count} channels | Status: {(isMonitoring ? "ACTIVE" : "READY")}";
            }

            // Switch to virtual mode if too many signals
            var lvLiveSignals = this.Controls.Find("lvLiveSignals", true)[0] as ListView;
            if (lvLiveSignals != null && allSignals.Count > 1000 && !lvLiveSignals.VirtualMode)
            {
                SetupOptimizedListView(lvLiveSignals, true);
                lvLiveSignals.VirtualListSize = allSignals.Count;
            }
        }

        private void UpdateTimeDisplays()
        {
            try
            {
                var lblSubtitle = this.Controls.Find("lblSubtitle", true).FirstOrDefault() as Label;
                if (lblSubtitle != null)
                {
                    var localTime = DateTime.Now;
                    var utcTime = DateTime.UtcNow;
                    lblSubtitle.Text = $"🕒 Local: {localTime:yyyy-MM-dd HH:mm:ss} | UTC: {utcTime:HH:mm:ss} | User: islamahmed9717";
                }
                if (isMonitoring && telegramService != null)
                {
                    Task.Run(async () =>
                    {
                        var stats = await telegramService.GetMonitoringStatistics();
                        this.BeginInvoke(new Action(() =>
                        {
                            var lblStats = this.Controls.Find("lblStats", true)[0] as Label;
                            if (lblStats != null)
                            {
                                lblStats.Text = $"📊 Live System | Channels: {stats.MonitoredChannelsCount} | " +
                                              $"Messages: {stats.TotalMessagesProcessed} | " +
                                              $"Rate: {stats.ChannelStatuses.Sum(c => c.MessageRate):F1}/min | " +
                                              $"Latency: {stats.AverageLatency:F0}ms";
                            }
                        }));
                    });
                }

                foreach (Control control in this.Controls)
                {
                    if (control is StatusStrip statusStrip)
                    {
                        foreach (ToolStripItem item in statusStrip.Items)
                        {
                            if (item.Name == "statusLabel")
                            {
                                item.Text = $"Real-time System Active | Local: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                                break;
                            }
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Display update error: {ex.Message}");
            }
        }

        private async Task ReloadChannels()
        {
            var btnConnect = this.Controls.Find("btnConnect", true)[0] as Button;
            var btnRefresh = this.Controls.Find("btnRefreshChannels", true)[0] as Button;

            if (btnConnect != null)
            {
                btnConnect.Text = "🔄 RELOADING...";
                btnConnect.Enabled = false;
            }

            if (btnRefresh != null)
            {
                btnRefresh.Enabled = false;
                btnRefresh.Text = "⏳";
            }

            try
            {
                LogMessage("🔄 Reloading channels from Telegram...");

                allChannels.Clear();
                var lvChannels = this.Controls.Find("lvChannels", true)[0] as ListView;
                if (lvChannels != null)
                {
                    lvChannels.Items.Clear();
                }

                var channels = await GetChannelsOptimized(true);
                allChannels = channels;

                UpdateChannelsList(channels);

                LogMessage($"✅ Channels reloaded successfully - Found {channels.Count} channels");

                ShowMessage($"✅ Channels reloaded successfully!\n\n📢 Found {channels.Count} channels",
                           "Channels Reloaded", MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowMessage($"❌ Failed to reload channels:\n\n{ex.Message}",
                           "Reload Error", MessageBoxIcon.Error);
                LogMessage($"❌ Channel reload failed: {ex.Message}");
            }
            finally
            {
                if (btnConnect != null)
                {
                    btnConnect.Text = "🔗 CONNECT";
                    btnConnect.Enabled = true;
                }

                if (btnRefresh != null)
                {
                    btnRefresh.Enabled = true;
                    btnRefresh.Text = "🔄";
                }
            }
        }

        private void UpdateChannelsList(List<ChannelInfo> channels)
        {
            allChannels = channels;
            RefreshChannelsList();
        }

        private void RefreshChannelsList()
        {
            var lvChannels = this.Controls.Find("lvChannels", true)[0] as ListView;
            if (lvChannels == null) return;

            lvChannels.Items.Clear();

            foreach (var channel in allChannels)
            {
                var item = new ListViewItem(channel.Title);
                item.SubItems.Add(channel.Id.ToString());
                item.SubItems.Add(channel.Type);
                item.SubItems.Add(channel.MembersCount.ToString());
                item.SubItems.Add(channel.LastActivity.ToString("HH:mm"));
                item.Tag = channel;

                // Color coding based on type
                switch (channel.Type.ToLower())
                {
                    case "vip":
                        item.BackColor = Color.FromArgb(255, 235, 59);
                        break;
                    case "premium":
                        item.BackColor = Color.FromArgb(156, 39, 176);
                        item.ForeColor = Color.White;
                        break;
                    case "signals":
                        item.BackColor = Color.FromArgb(76, 175, 80);
                        item.ForeColor = Color.White;
                        break;
                    case "gold":
                        item.BackColor = Color.FromArgb(255, 193, 7);
                        break;
                    case "crypto":
                        item.BackColor = Color.FromArgb(255, 87, 34);
                        item.ForeColor = Color.White;
                        break;
                    default:
                        item.BackColor = Color.FromArgb(200, 230, 255);
                        break;
                }

                lvChannels.Items.Add(item);
            }

            ApplyChannelFilters();
            UpdateChannelsCount();
        }

        private void ApplyChannelFilters()
        {
            var lvChannels = this.Controls.Find("lvChannels", true)[0] as ListView;
            var txtSearch = this.Controls.Find("txtSearch", true)[0] as TextBox;
            var cmbFilter = this.Controls.Find("cmbFilter", true)[0] as ComboBox;

            if (lvChannels == null) return;

            var searchText = txtSearch?.Text?.ToLower() ?? "";
            var filterType = cmbFilter?.SelectedItem?.ToString() ?? "All Types";

            foreach (ListViewItem item in lvChannels.Items)
            {
                var channel = item.Tag as ChannelInfo;
                if (channel == null) continue;

                bool visible = true;

                if (!string.IsNullOrEmpty(searchText))
                {
                    visible = channel.Title.ToLower().Contains(searchText) ||
                             channel.Id.ToString().Contains(searchText) ||
                             channel.Type.ToLower().Contains(searchText);
                }

                if (visible && filterType != "All Types")
                {
                    visible = channel.Type.Equals(filterType, StringComparison.OrdinalIgnoreCase);
                }

                item.Font = visible ? new Font("Segoe UI", 9F) : new Font("Segoe UI", 9F, FontStyle.Strikeout);
                item.ForeColor = visible ? item.ForeColor : Color.Gray;
            }
        }

        private void SearchAndHighlightChannel(string searchTerm)
        {
            var lvChannels = this.Controls.Find("lvChannels", true)[0] as ListView;
            if (lvChannels == null) return;

            searchTerm = searchTerm.ToLower();
            bool found = false;

            foreach (ListViewItem item in lvChannels.Items)
            {
                var channel = item.Tag as ChannelInfo;
                if (channel == null) continue;

                string channelTitle = channel.Title.ToLower();
                string channelUsername = channel.Username?.ToLower() ?? "";

                if (channelTitle.Contains(searchTerm) || channelUsername.Contains(searchTerm))
                {
                    item.BackColor = Color.FromArgb(255, 255, 100);
                    item.Font = new Font(item.Font, FontStyle.Bold);

                    if (!found)
                    {
                        item.EnsureVisible();
                        item.Selected = true;
                        found = true;
                    }
                }
                else
                {
                    item.Font = new Font("Segoe UI", 9F);
                    RestoreChannelItemColor(item, channel);
                }
            }

            if (!found)
            {
                LogMessage($"⚠️ Channel containing '{searchTerm}' not found. Try reloading channels.");
            }
            else
            {
                LogMessage($"✅ Found and highlighted channels containing '{searchTerm}'");
            }
        }

        private void RestoreChannelItemColor(ListViewItem item, ChannelInfo channel)
        {
            switch (channel.Type.ToLower())
            {
                case "vip":
                    item.BackColor = Color.FromArgb(255, 235, 59);
                    break;
                case "premium":
                    item.BackColor = Color.FromArgb(156, 39, 176);
                    item.ForeColor = Color.White;
                    break;
                case "signals":
                    item.BackColor = Color.FromArgb(76, 175, 80);
                    item.ForeColor = Color.White;
                    break;
                case "gold":
                    item.BackColor = Color.FromArgb(255, 193, 7);
                    break;
                case "crypto":
                    item.BackColor = Color.FromArgb(255, 87, 34);
                    item.ForeColor = Color.White;
                    break;
                default:
                    item.BackColor = Color.FromArgb(200, 230, 255);
                    item.ForeColor = Color.Black;
                    break;
            }
        }

        private void LogMessage(string message)
        {
            try
            {
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(() => LogMessageInternal(message)));
                }
                else
                {
                    LogMessageInternal(message);
                }
            }
            catch
            {
                // Ignore logging errors to prevent crashes
            }
        }

        private void LogMessageInternal(string message)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
        }

        private void ShowMessage(string message, string title, MessageBoxIcon icon)
        {
            MessageBox.Show(message, title, MessageBoxButtons.OK, icon);
        }

        private bool IsValidPhoneNumber(string phoneNumber)
        {
            if (string.IsNullOrEmpty(phoneNumber))
                return false;

            var cleanPhone = phoneNumber.Replace(" ", "").Replace("-", "");
            if (!cleanPhone.StartsWith("+"))
                return false;

            var digits = cleanPhone.Substring(1);
            return digits.Length >= 10 && digits.Length <= 15 && digits.All(char.IsDigit);
        }

        private string AutoDetectMT4Path()
        {
            try
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var possiblePaths = new[]
                {
                    Path.Combine(userProfile, "AppData", "Roaming", "MetaQuotes", "Terminal"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MetaTrader 4", "MQL4", "Files"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MetaTrader 4", "MQL4", "Files"),
                    Path.Combine(userProfile, "Documents", "MT4", "Files"),
                    Path.Combine(userProfile, "Documents", "MT5", "Files")
                };

                foreach (var basePath in possiblePaths)
                {
                    if (Directory.Exists(basePath))
                    {
                        var directories = Directory.GetDirectories(basePath);
                        foreach (var dir in directories)
                        {
                            var mql4Files = Path.Combine(dir, "MQL4", "Files");
                            var mql5Files = Path.Combine(dir, "MQL5", "Files");

                            if (Directory.Exists(mql4Files))
                                return mql4Files;
                            if (Directory.Exists(mql5Files))
                                return mql5Files;
                        }

                        return basePath;
                    }
                }

                return Path.Combine(userProfile, "Documents", "MT4", "Files");
            }
            catch
            {
                return "";
            }
        }

        private string GenerateEAConfiguration()
        {
            var channelIds = string.Join(",", selectedChannels.Select(c => c.Id.ToString()));

            return $@"//+------------------------------------------------------------------+
//|                    Telegram EA Configuration                     |
//|                Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss} UTC               |
//|                User: islamahmed9717                              |
//+------------------------------------------------------------------+

//--- Telegram Channel Settings ---
ChannelIDs = ""{channelIds}""
SignalFilePath = ""TelegramSignals.txt""

//--- Risk Management Settings ---
RiskMode = ""Fixed""
FixedLotSize = 0.01
RiskPercent = 2.0
RiskAmount = 100

//--- Symbol Mapping Settings ---
SymbolsMapping = ""EURUSD:EURUSD,GBPUSD:GBPUSD,USDJPY:USDJPY,GOLD:XAUUSD,SILVER:XAGUSD,BITCOIN:BTCUSD""
SymbolPrefix = """"
SymbolSuffix = """"

//--- Advanced Settings ---
UseTrailingStop = false
TrailingStartPips = 10
TrailingStepPips = 5
MoveSLToBreakeven = true
BreakevenAfterPips = 10
SendNotifications = true
MaxSpreadPips = 5
SignalCheckInterval = 5
ForceMarketExecution = true
MaxRetriesOrderSend = 3

//--- Selected Channels ---
/*
{string.Join("\n", selectedChannels.Select(c => $"Channel: {c.Title} (ID: {c.Id}) - Type: {c.Type} - Members: {c.MembersCount}"))}
*/

//--- Configuration Instructions ---
/*
1. Copy the above settings into your Telegram EA input parameters
2. Make sure the MT4/MT5 Files path is set correctly in this app
3. Ensure this Windows application is running and monitoring channels
4. The EA will automatically read signals from: TelegramSignals.txt
5. Start monitoring in this app before running the EA

Generated on: {DateTime.Now:yyyy-MM-dd HH:mm:ss} UTC
By: islamahmed9717 - Telegram EA Manager v2.0 (Real Implementation)
System: Windows Forms .NET 9.0 with WTelegramClient
*/";
        }

        private void SavePhoneNumber(string phoneNumber)
        {
            try
            {
                var settings = LoadAppSettings();
                if (!settings.SavedAccounts.Contains(phoneNumber))
                {
                    settings.SavedAccounts.Add(phoneNumber);
                }
                settings.LastPhoneNumber = phoneNumber;
                SaveAppSettings(settings);

                var cmbPhone = this.Controls.Find("cmbPhone", true)[0] as ComboBox;
                if (cmbPhone != null && !cmbPhone.Items.Contains(phoneNumber))
                {
                    cmbPhone.Items.Add(phoneNumber);
                }
            }
            catch
            {
                // Ignore save errors
            }
        }

        private void SaveMT4Path(string path)
        {
            try
            {
                var settings = LoadAppSettings();
                settings.MT4Path = path;
                SaveAppSettings(settings);
            }
            catch
            {
                // Ignore save errors
            }
        }

        private AppSettings LoadAppSettings()
        {
            try
            {
                if (File.Exists("app_settings.json"))
                {
                    var json = File.ReadAllText("app_settings.json");
                    return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch
            {
                // Return default settings on error
            }
            return new AppSettings();
        }

        private void SaveAppSettings(AppSettings settings)
        {
            try
            {
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText("app_settings.json", json);
            }
            catch
            {
                // Ignore save errors
            }
        }

        private void LoadApplicationSettings()
        {
            try
            {
                if (File.Exists("app_settings.json"))
                {
                    var json = File.ReadAllText("app_settings.json");
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                    if (settings != null)
                    {
                        var cmbPhone = this.Controls.Find("cmbPhone", true)[0] as ComboBox;
                        var txtMT4Path = this.Controls.Find("txtMT4Path", true)[0] as TextBox;

                        if (settings.SavedAccounts?.Count > 0 && cmbPhone != null)
                        {
                            cmbPhone.Items.AddRange(settings.SavedAccounts.ToArray());
                            cmbPhone.Text = settings.LastPhoneNumber;
                        }

                        if (!string.IsNullOrEmpty(settings.MT4Path) && txtMT4Path != null)
                        {
                            txtMT4Path.Text = settings.MT4Path;
                        }
                    }
                }

                // Load EA settings
                eaSettings = signalProcessor.GetEASettings();
            }
            catch
            {
                // Ignore loading errors
            }
        }

        private void SaveApplicationSettings()
        {
            try
            {
                var settings = new AppSettings
                {
                    SavedAccounts = new List<string>(),
                    LastPhoneNumber = phoneNumber,
                    MT4Path = eaSettings?.MT4FilesPath ?? "",
                    LastUsed = DateTime.Now
                };

                var cmbPhone = this.Controls.Find("cmbPhone", true).FirstOrDefault() as ComboBox;
                if (cmbPhone != null)
                {
                    foreach (var item in cmbPhone.Items)
                    {
                        settings.SavedAccounts.Add(item.ToString());
                    }
                }

                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText("app_settings.json", json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving app settings: {ex.Message}");
            }
        }

        private void ShowPerformanceReport()
        {
            var report = perfMonitor.GetReport();
            MessageBox.Show(report, "Performance Report", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void CleanupRecentSignalsTracker()
        {
            var cutoffTime = DateTime.Now.AddMinutes(-5);
            var keysToRemove = recentSignalsInUI
                .Where(kvp => kvp.Value < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                recentSignalsInUI.TryRemove(key, out _);
            }
        }

        private void CleanupResources()
        {
            // Clear large collections periodically
            if (allSignals.Count > 5000)
            {
                var recentSignals = allSignals
                    .OrderByDescending(s => s.DateTime)
                    .Take(1000)
                    .ToList();

                allSignals.Clear();
                allSignals.AddRange(recentSignals);

                // Force garbage collection for large cleanups
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        private async Task RecoverFromError(Exception ex)
        {
            LogMessage($"🔧 Attempting to recover from error: {ex.Message}");

            try
            {
                // Stop current operations
                if (isMonitoring)
                {
                    telegramService.StopMonitoring();
                    await Task.Delay(1000);
                }

                // Clear problematic data
                recentSignalsInUI.Clear();

                // Restart monitoring if it was active
                if (isMonitoring && selectedChannels.Count > 0)
                {
                    telegramService.StartEnhancedMonitoring(selectedChannels);
                    LogMessage("✅ Monitoring restarted successfully");
                }
            }
            catch (Exception recoveryEx)
            {
                LogMessage($"❌ Recovery failed: {recoveryEx.Message}");
            }
        }

        private async Task StartHealthMonitoring()
        {
            while (!this.IsDisposed)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1));

                    if (isMonitoring)
                    {
                        var health = new
                        {
                            SignalQueueSize = signalProcessor?.GetProcessedSignalsCount() ?? 0,
                            PendingSignals = pendingSignals?.Count ?? 0,
                            TotalSignals = allSignals?.Count ?? 0,
                            Memory = GC.GetTotalMemory(false) / 1024 / 1024 // MB
                        };

                        LogDebugMessage($"📊 Health Check - Queue: {health.SignalQueueSize}, " +
                                      $"Pending: {health.PendingSignals}, " +
                                      $"Signals: {health.TotalSignals}, Memory: {health.Memory}MB");

                        // Trigger cleanup if memory usage is high
                        if (health.Memory > 500) // 500MB threshold
                        {
                            CleanupResources();
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogDebugMessage($"Health check error: {ex.Message}");
                }
            }
        }
        #endregion

        #region File Monitoring
        private void StartAutoCleanup()
        {
            cleanupTimer?.Stop();
            cleanupTimer?.Dispose();

            cleanupTimer = new System.Windows.Forms.Timer
            {
                Interval = 300000 // 5 minutes
            };

            cleanupTimer.Tick += (s, e) =>
            {
                try
                {
                    signalProcessor.CleanupProcessedSignals();
                    LogMessage("🧹 Auto-cleanup completed - removed old/processed signals");
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ Auto-cleanup error: {ex.Message}");
                }
            };

            cleanupTimer.Start();
            LogMessage("🧹 Auto-cleanup started (runs every 5 minutes)");
        }

        private void StartSignalFileMonitoring(string mt4Path)
        {
            try
            {
                var signalFilePath = Path.Combine(mt4Path, "telegram_signals.txt");
                var directory = Path.GetDirectoryName(signalFilePath);

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                StopSignalFileMonitoring();

                signalFileWatcher = new FileSystemWatcher
                {
                    Path = directory,
                    Filter = "telegram_signals.txt",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 65536 // 64KB buffer
                };

                signalFileWatcher.Changed += async (sender, e) => await OnSignalFileChangedAsync(sender, e);
                signalFileWatcher.Created += async (sender, e) => await OnSignalFileChangedAsync(sender, e);
                signalFileWatcher.EnableRaisingEvents = true;

                LogMessage($"📁 Started monitoring signal file: {signalFilePath}");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Failed to start file monitoring: {ex.Message}");
            }
        }

        private async Task OnSignalFileChangedAsync(object sender, FileSystemEventArgs e)
        {
            try
            {
                await Task.Delay(200);

                await Task.Run(() =>
                {
                    try
                    {
                        if (this.InvokeRequired)
                        {
                            this.BeginInvoke(new Action(() => {
                                LogMessage($"📝 Signal file updated: {e.ChangeType} at {DateTime.Now:HH:mm:ss}");
                                UpdateFileStatus(e.FullPath);
                            }));
                        }
                        else
                        {
                            LogMessage($"📝 Signal file updated: {e.ChangeType} at {DateTime.Now:HH:mm:ss}");
                            UpdateFileStatus(e.FullPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"File monitoring error: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error monitoring file: {ex.Message}");
            }
        }

        private void StopSignalFileMonitoring()
        {
            if (signalFileWatcher != null)
            {
                signalFileWatcher.EnableRaisingEvents = false;
                signalFileWatcher.Dispose();
                signalFileWatcher = null;
            }
        }

        private void UpdateFileStatus(string filePath)
        {
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(100);

                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Exists)
                    {
                        string lastSignalInfo = "";

                        for (int i = 0; i < 3; i++)
                        {
                            try
                            {
                                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                using (var reader = new StreamReader(fs))
                                {
                                    var lines = new List<string>();
                                    string line;
                                    while ((line = await reader.ReadLineAsync()) != null)
                                    {
                                        lines.Add(line);
                                    }

                                    var lastSignalLine = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"));
                                    if (!string.IsNullOrEmpty(lastSignalLine))
                                    {
                                        var parts = lastSignalLine.Split('|');
                                        if (parts.Length >= 11)
                                        {
                                            var timestamp = parts[0];
                                            var channel = parts[2];
                                            var symbol = parts[4];
                                            var direction = parts[3];
                                            var status = parts[10];

                                            lastSignalInfo = $"📊 Last signal: {symbol} {direction} from {channel} at {timestamp} - Status: {status}";
                                        }
                                    }
                                }
                                break;
                            }
                            catch (IOException)
                            {
                                if (i < 2) await Task.Delay(100);
                                else throw;
                            }
                        }

                        if (this.InvokeRequired)
                        {
                            this.BeginInvoke(new Action(() => {
                                if (!string.IsNullOrEmpty(lastSignalInfo))
                                    LogMessage(lastSignalInfo);

                                var lblStats = this.Controls.Find("lblStats", true)[0] as Label;
                                if (lblStats != null)
                                {
                                    lblStats.Text = $"📊 Live System | Signals: {allSignals.Count} | File: {fileInfo.Length:N0} bytes | Last update: {fileInfo.LastWriteTime:HH:mm:ss}";
                                }
                            }));
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ Error reading file status: {ex.Message}");
                }
            });
        }

        private void ClearOldSignalsFromFile()
        {
            var txtMT4Path = this.Controls.Find("txtMT4Path", true)[0] as TextBox;
            var mt4Path = txtMT4Path?.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(mt4Path) || !Directory.Exists(mt4Path))
                return;

            try
            {
                var signalFilePath = Path.Combine(mt4Path, "telegram_signals.txt");

                if (File.Exists(signalFilePath))
                {
                    var lines = File.ReadAllLines(signalFilePath).ToList();
                    var newLines = new List<string>();
                    var now = DateTime.Now;

                    foreach (var line in lines)
                    {
                        if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                        {
                            newLines.Add(line);
                        }
                        else
                        {
                            var parts = line.Split('|');
                            if (parts.Length >= 11)
                            {
                                var timestampStr = parts[0];
                                if (DateTime.TryParse(timestampStr, out DateTime signalTime))
                                {
                                    var ageMinutes = (now - signalTime).TotalMinutes;
                                    if (ageMinutes <= 60)
                                    {
                                        newLines.Add(line);
                                    }
                                    else
                                    {
                                        LogMessage($"🧹 Removing old signal: {parts[4]} {parts[3]} - Age: {ageMinutes:F1} minutes");
                                    }
                                }
                            }
                        }
                    }

                    File.WriteAllLines(signalFilePath, newLines);

                    LogMessage($"🧹 Cleaned signal file - removed old signals, kept {newLines.Count(l => !l.StartsWith("#") && !string.IsNullOrWhiteSpace(l))} recent signals");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Error cleaning signal file: {ex.Message}");
            }
        }

        private void CheckSignalFile()
        {
            var txtMT4Path = this.Controls.Find("txtMT4Path", true)[0] as TextBox;
            var mt4Path = txtMT4Path?.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(mt4Path))
            {
                ShowMessage("❌ Please set MT4/MT5 path first!", "No Path", MessageBoxIcon.Warning);
                return;
            }

            var signalFilePath = Path.Combine(mt4Path, "telegram_signals.txt");

            if (!File.Exists(signalFilePath))
            {
                ShowMessage($"❌ Signal file not found!\n\n{signalFilePath}", "File Not Found", MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var lines = File.ReadAllLines(signalFilePath);
                var signalLines = lines.Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#")).ToList();

                var fileInfo = new FileInfo(signalFilePath);

                var report = $"📁 SIGNAL FILE REPORT:\n\n" +
                            $"📍 Path: {signalFilePath}\n" +
                            $"📏 Size: {fileInfo.Length} bytes\n" +
                            $"🕒 Last Modified: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}\n" +
                            $"📊 Total Lines: {lines.Length}\n" +
                            $"📈 Signal Lines: {signalLines.Count}\n\n";

                if (signalLines.Count > 0)
                {
                    report += "📋 LAST 5 SIGNALS:\n\n";
                    var lastSignals = signalLines.TakeLast(5).Reverse();

                    foreach (var line in lastSignals)
                    {
                        var parts = line.Split('|');
                        if (parts.Length >= 5)
                        {
                            report += $"• {parts[0]} - {parts[4]} {parts[3]} from {parts[2]}\n";
                        }
                    }
                }
                else
                {
                    report += "⚠️ No signals found in file!";
                }

                ShowMessage(report, "Signal File Status", MessageBoxIcon.Information);

                try
                {
                    System.Diagnostics.Process.Start("notepad.exe", signalFilePath);
                }
                catch { }
            }
            catch (Exception ex)
            {
                ShowMessage($"❌ Error reading file:\n\n{ex.Message}", "Error", MessageBoxIcon.Error);
            }
        }
        #endregion

        #region Debug Console
        private void CreateDebugConsole()
        {
            try
            {
                if (debugForm != null && !debugForm.IsDisposed)
                {
                    debugForm.Close();
                    debugForm.Dispose();
                    debugForm = null;
                }

                debugForm = new Form
                {
                    Text = "🐛 Debug Console - Telegram EA Manager - islamahmed9717",
                    Size = new Size(1000, 600),
                    StartPosition = FormStartPosition.Manual,
                    FormBorderStyle = FormBorderStyle.Sizable,
                    MinimizeBox = true,
                    MaximizeBox = true,
                    ShowInTaskbar = true,
                    BackColor = Color.Black,
                    Icon = this.Icon
                };

                debugForm.Location = new Point(this.Location.X + this.Width + 10, this.Location.Y);

                var mainPanel = new Panel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(5)
                };
                debugForm.Controls.Add(mainPanel);

                var headerPanel = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = 40,
                    BackColor = Color.FromArgb(37, 99, 235)
                };
                mainPanel.Controls.Add(headerPanel);

                var lblHeader = new Label
                {
                    Text = $"🐛 REAL-TIME DEBUG CONSOLE | Started: {DateTime.Now:HH:mm:ss} | User: islamahmed9717",
                    Dock = DockStyle.Fill,
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(10, 0, 0, 0)
                };
                headerPanel.Controls.Add(lblHeader);

                debugConsole = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ScrollBars = ScrollBars.Both,
                    BackColor = Color.Black,
                    ForeColor = Color.Lime,
                    Font = new Font("Consolas", 9F),
                    ReadOnly = true,
                    WordWrap = false,
                    Margin = new Padding(5)
                };
                mainPanel.Controls.Add(debugConsole);

                var buttonPanel = new Panel
                {
                    Dock = DockStyle.Bottom,
                    Height = 50,
                    BackColor = Color.FromArgb(30, 30, 30)
                };
                mainPanel.Controls.Add(buttonPanel);

                var btnClear = new Button
                {
                    Text = "🗑️ Clear",
                    Location = new Point(10, 10),
                    Size = new Size(80, 30),
                    BackColor = Color.FromArgb(220, 38, 38),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 9F)
                };
                btnClear.Click += (s, e) => {
                    if (debugConsole != null && !debugConsole.IsDisposed)
                    {
                        debugConsole.Clear();
                        LogDebugMessage("🗑️ Debug console cleared");
                    }
                };
                buttonPanel.Controls.Add(btnClear);

                var btnSave = new Button
                {
                    Text = "💾 Save Log",
                    Location = new Point(100, 10),
                    Size = new Size(90, 30),
                    BackColor = Color.FromArgb(34, 197, 94),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 9F)
                };
                btnSave.Click += (s, e) => SaveDebugLog();
                buttonPanel.Controls.Add(btnSave);

                var chkAutoScroll = new CheckBox
                {
                    Text = "Auto-scroll",
                    Location = new Point(200, 15),
                    Size = new Size(100, 20),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9F),
                    Checked = true,
                    Name = "chkAutoScroll"
                };
                buttonPanel.Controls.Add(chkAutoScroll);

                var lblStatus = new Label
                {
                    Text = "Debug console ready...",
                    Location = new Point(320, 15),
                    Size = new Size(400, 20),
                    ForeColor = Color.Gray,
                    Font = new Font("Segoe UI", 8F)
                };
                buttonPanel.Controls.Add(lblStatus);

                debugForm.FormClosing += (s, e) => {
                    isDebugFormOpen = false;
                    debugConsole = null;
                    debugForm = null;
                };

                debugForm.Show();
                isDebugFormOpen = true;

                LogDebugMessage("🚀 DEBUG CONSOLE STARTED");
                LogDebugMessage($"📅 Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                LogDebugMessage($"👤 User: islamahmed9717");
                LogDebugMessage($"🔗 Connected to Telegram: {(telegramService?.IsUserAuthorized() ?? false)}");
                LogDebugMessage($"📊 Monitoring: {(isMonitoring ? "ACTIVE" : "STOPPED")}");
                LogDebugMessage("═══════════════════════════════════════");

                LogMessage("✅ Debug console opened successfully");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Failed to open debug console:\n\n{ex.Message}",
                               "Debug Console Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LogDebugMessage(string message)
        {
            try
            {
                Console.WriteLine($"[DEBUG] {message}");

                if (debugConsole != null && !debugConsole.IsDisposed && isDebugFormOpen)
                {
                    var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                    var formattedMessage = $"[{timestamp}] {message}";

                    if (debugConsole.InvokeRequired)
                    {
                        try
                        {
                            debugConsole.Invoke(new Action(() => {
                                AppendToDebugConsole(formattedMessage);
                            }));
                        }
                        catch (InvalidOperationException)
                        {
                            // Control is being disposed, ignore
                        }
                    }
                    else
                    {
                        AppendToDebugConsole(formattedMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Debug console error: {ex.Message}");
            }
        }

        private void AppendToDebugConsole(string message)
        {
            try
            {
                if (debugConsole == null || debugConsole.IsDisposed) return;

                debugConsole.AppendText(message + Environment.NewLine);

                // Auto-scroll if enabled
                var chkAutoScroll = debugForm?.Controls.Find("chkAutoScroll", true).FirstOrDefault() as CheckBox;
                if (chkAutoScroll?.Checked == true)
                {
                    debugConsole.SelectionStart = debugConsole.Text.Length;
                    debugConsole.ScrollToCaret();
                }

                // Limit text length to prevent memory issues
                if (debugConsole.Text.Length > 100000)
                {
                    var lines = debugConsole.Lines;
                    if (lines.Length > 1000)
                    {
                        var keepLines = lines.Skip(lines.Length - 800).ToArray();
                        debugConsole.Text = string.Join(Environment.NewLine, keepLines);
                        debugConsole.AppendText(Environment.NewLine + "... (older messages truncated) ..." + Environment.NewLine);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Append error: {ex.Message}");
            }
        }

        private void SaveDebugLog()
        {
            try
            {
                if (debugConsole == null || debugConsole.IsDisposed || string.IsNullOrEmpty(debugConsole.Text))
                {
                    MessageBox.Show("❌ No debug data to save!", "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var saveDialog = new SaveFileDialog
                {
                    Filter = "Text files (*.txt)|*.txt|Log files (*.log)|*.log|All files (*.*)|*.*",
                    FileName = $"TelegramEA_Debug_islamahmed9717_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    Title = "Save Debug Log"
                };

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    var logContent = $"# Telegram EA Manager Debug Log\r\n" +
                                   $"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                                   $"# User: islamahmed9717\r\n" +
                                   $"# System: {Environment.OSVersion}\r\n" +
                                   $"# .NET Version: {Environment.Version}\r\n" +
                                   $"#" + new string('=', 50) + "\r\n\r\n" +
                                   debugConsole.Text;

                    File.WriteAllText(saveDialog.FileName, logContent);

                    MessageBox.Show($"✅ Debug log saved successfully!\n\n📁 File: {saveDialog.FileName}\n📊 Size: {new FileInfo(saveDialog.FileName).Length} bytes",
                                   "Log Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Failed to save debug log:\n\n{ex.Message}",
                               "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        #endregion

        #region Form Lifecycle
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Ensure all controls are properly initialized
            EnsureControlsInitialized();

            // Subscribe to events
            if (signalProcessor != null)
            {
                signalProcessor.DebugMessage += SignalProcessor_DebugMessage;
            }

            if (telegramService != null)
            {
                telegramService.DebugMessage += TelegramService_DebugMessage;
            }
        }

        private void EnsureControlsInitialized()
        {
            var controlsToCheck = new[] { "lvLiveSignals", "lvSelected", "lvChannels" };

            foreach (var controlName in controlsToCheck)
            {
                var controls = this.Controls.Find(controlName, true);
                if (controls.Length == 0)
                {
                    MessageBox.Show($"Critical control '{controlName}' not found! UI may not function properly.",
                                   "Initialization Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                // Cancel all operations
                monitoringCts?.Cancel();
                if (monitoringDashboard != null && !monitoringDashboard.IsDisposed)
                {
                    monitoringDashboard.Close();
                }

                if (telegramService != null && isMonitoring)
                {
                    telegramService.StopMonitoring().Wait(5000);
                }

                // Stop all timers with null checks
                uiUpdateTimer?.Stop();
                uiUpdateTimer?.Dispose();

                cleanupTimer?.Stop();
                cleanupTimer?.Dispose();

                liveFeedUpdateTimer?.Stop();
                liveFeedUpdateTimer?.Dispose();

                uiCleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                uiCleanupTimer?.Dispose();

                // Stop file monitoring
                StopSignalFileMonitoring();

                // Stop telegram monitoring
                if (telegramService != null)
                {
                    telegramService.StopMonitoring();
                    telegramService.Dispose();
                }

                // Dispose signal processor - this will save history
                signalProcessor?.Dispose();

                // Dispose semaphores
                uiUpdateSemaphore?.Dispose();

                // Clear collections
                recentSignalsInUI?.Clear();
                allSignals?.Clear();
                selectedChannels?.Clear();
                pendingSignals?.Clear();

                // Save application settings
                SaveApplicationSettings();

                // Force final garbage collection
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during cleanup: {ex.Message}");
            }

            base.OnFormClosing(e);
        }
        #endregion
    }
}