using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Zvt2SumUp.Core;
using Zvt2SumUp.Infrastructure;

namespace Zvt2SumUp.Desktop;

internal sealed class MainForm : Form
{
    private readonly RuntimeHostController runtime = new(); private readonly BrandTabControl tabs = new();
    private Panel sidebar = null!;
    private readonly Label gatewayStatus = StatusLabel("GESTOPPT", Theme.Muted), serviceStatus = StatusLabel("PRÜFE...", Theme.Muted), sumUpStatus = StatusLabel("NICHT GEPRÜFT", Theme.Muted);
    private readonly Label sidebarGatewayStatus = StatusLabel("GESTOPPT", Theme.Muted), sidebarServiceStatus = StatusLabel("PRÜFE...", Theme.Muted), sidebarSumUpStatus = StatusLabel("NICHT GEPRÜFT", Theme.Muted);
    private readonly Label endpointStatus = StatusLabel("127.0.0.1:20007", Theme.BlueBright), cashRegisterStatus = StatusLabel("GETRENNT", Theme.Muted),
        selectedTerminalStatus = StatusLabel("NICHT AUSGEWÄHLT", Theme.Muted), lastPaymentStatus = StatusLabel("NOCH KEINE", Theme.Muted), journalStatus = StatusLabel("0 OFFEN", Theme.Green);
    private readonly TextBox apiKey = Input(true), affiliateKey = Input(true), affiliateApp = Input(), merchantCode = Input(), terminalId = Input(), host = Input(), port = Input(),
        idleTimeout = Input(), comPort = Input(), comBaud = Input(), currency = Input(), logFile = Input(), timeout = Input(), updateRepository = Input();
    private readonly ComboBox mode = new(), logLevel = new(), endOfDaySource = new();
    private readonly CheckBox revealSecrets = new(), resetAfterReconciliation = new(); private readonly ListView terminalList = List(), transactionList = List();
    private readonly RichTextBox logBox = new(), receiptEditor = new(), receiptPreview = new(); private readonly Label warning = new();
    private readonly System.Windows.Forms.Timer logTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer secretRevealTimer = new() { Interval = 10_000 };
    private bool closing; private string lastLogText = string.Empty;

    public MainForm(bool smokeTest = false)
    {
        Text = "ZVT2SumUp - Payment Gateway"; ClientSize = new Size(1220, 760); MinimumSize = new Size(960, 640); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background; ForeColor = Theme.Ink; Font = Theme.Body; AutoScaleMode = AutoScaleMode.Dpi;
        Icon = LoadIcon();
        Controls.Add(tabs); sidebar = BuildSidebar(); Controls.Add(sidebar); BuildTabs();
        SizeChanged += (_, _) => UpdateResponsiveShell(); UpdateResponsiveShell();
        Deactivate += (_, _) => revealSecrets.Checked = false;
        if (!smokeTest) Shown += async (_, _) => { Theme.DarkTitleBar(this); await LoadOptionsAsync(); await RefreshServiceStatusAsync(); };
        FormClosing += FormIsClosing; logTimer.Tick += async (_, _) => { RefreshLogs(); await RefreshOverviewAsync(); };
        secretRevealTimer.Tick += (_, _) => revealSecrets.Checked = false;
        if (!smokeTest) logTimer.Start();
    }

    private Panel BuildSidebar()
    {
        Panel panel = new() { Dock = DockStyle.Left, Width = 320, BackColor = Theme.Sidebar, Padding = new Padding(14) };
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, RowCount = 12, ColumnCount = 1, BackColor = Theme.Sidebar };
        layout.RowStyles.Add(new(SizeType.Absolute, 96)); for (int i = 1; i < 9; i++) layout.RowStyles.Add(new(SizeType.Absolute, i is 2 or 4 or 6 ? 26 : 41)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 42)); layout.RowStyles.Add(new(SizeType.Absolute, 42));
        Panel brand = new() { Dock = DockStyle.Fill, BackColor = Theme.Sidebar }; brand.Controls.Add(new BridgeLogo { Location = new Point(0, 5), Size = new Size(56, 56) });
        brand.Controls.Add(new WordmarkControl { Location = new Point(63, 2), Size = new Size(157, 54) });
        brand.Controls.Add(new Label { Text = "PAYMENT GATEWAY", Font = new("Consolas", 9, FontStyle.Bold), ForeColor = Theme.BlueBright, AutoSize = true, Location = new Point(66, 60) }); layout.Controls.Add(brand);
        layout.Controls.Add(Section("GATEWAY")); layout.Controls.Add(sidebarGatewayStatus); layout.Controls.Add(Section("SUMUP")); layout.Controls.Add(sidebarSumUpStatus); layout.Controls.Add(Section("WINDOWS-DIENST")); layout.Controls.Add(sidebarServiceStatus);
        Button start = Theme.Button("Gateway starten", 250, true), stop = Theme.Button("Gateway stoppen", 250); start.Dock = DockStyle.Fill; stop.Dock = DockStyle.Fill;
        start.Margin = new Padding(3); stop.Margin = new Padding(3); start.Click += async (_, _) => await UiAsync(StartGatewayAsync); stop.Click += async (_, _) => await UiAsync(StopGatewayAsync);
        layout.Controls.Add(start); layout.Controls.Add(stop); layout.Controls.Add(new Panel());
        Button logs = Theme.Button("Logs"), data = Theme.Button("Daten"); logs.Click += (_, _) => Open(AppPaths.Logs); data.Click += (_, _) => Open(AppPaths.Root); layout.Controls.Add(ButtonPair(logs, data));
        Button updates = Theme.Button("Updates"), about = Theme.Button("Sicherheit"); updates.Click += async (_, _) => await UiAsync(CheckUpdatesAsync); about.Click += (_, _) => ShowSafety(); layout.Controls.Add(ButtonPair(updates, about)); panel.Controls.Add(layout); return panel;
    }

    private void BuildTabs()
    {
        tabs.Dock = DockStyle.Fill;
        tabs.TabPages.Add(Page("Übersicht", BuildOverview())); tabs.TabPages.Add(Page("Einrichtung", BuildSetup())); tabs.TabPages.Add(Page("SumUp-Terminals", BuildTerminals()));
        tabs.TabPages.Add(Page("Transaktionen", BuildTransactions())); tabs.TabPages.Add(Page("Belege", BuildReceipts())); tabs.TabPages.Add(Page("Live-Log", BuildLog()));
        tabs.TabPages.Add(Page("Diagnose", BuildDiagnostics())); tabs.TabPages.Add(Page("Dienst", BuildService()));
    }
    private FlowLayoutPanel BuildOverview()
    {
        FlowLayoutPanel cards = new ResponsiveCardPanel { Dock = DockStyle.Fill, Padding = new Padding(22), BackColor = Theme.Background, AutoScroll = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight };
        cards.Controls.Add(Card("WINDOWS-DIENST", serviceStatus, "Installations- und Laufstatus")); cards.Controls.Add(Card("GATEWAY", gatewayStatus, "Native ZVT 13.13 Engine"));
        cards.Controls.Add(Card("ENDPUNKT", endpointStatus, "TCP oder COM")); cards.Controls.Add(Card("KASSENSYSTEM", cashRegisterStatus, "Verbindung wird live protokolliert"));
        cards.Controls.Add(Card("SUMUP", sumUpStatus, "Cloud API ohne POST-Retries")); cards.Controls.Add(Card("TERMINAL", selectedTerminalStatus, "Solo / Reader / klassisch"));
        cards.Controls.Add(Card("LETZTE ZAHLUNG", lastPaymentStatus, "Aus dem lokalen Journal")); cards.Controls.Add(Card("JOURNAL", journalStatus, "Terminalbezogene offene Posten"));
        return cards;
    }
    private TableLayoutPanel BuildSetup()
    {
        TableLayoutPanel form = FormLayout(); mode.Items.AddRange(["TCP", "COM"]); Theme.Input(mode); mode.DropDownStyle = ComboBoxStyle.DropDownList;
        logLevel.Items.AddRange(["Trace", "Debug", "Information", "Warning", "Error", "Critical"]); Theme.Input(logLevel); logLevel.DropDownStyle = ComboBoxStyle.DropDownList;
        endOfDaySource.Items.Add("local_journal"); Theme.Input(endOfDaySource); endOfDaySource.DropDownStyle = ComboBoxStyle.DropDownList;
        AddField(form, "Betriebsart", mode); AddField(form, "TCP-Bind-Adresse", host); AddField(form, "TCP-Port", port); AddField(form, "TCP-Idle-Timeout (s)", idleTimeout);
        AddField(form, "COM-Port", comPort); AddField(form, "COM-Baudrate", comBaud); AddField(form, "Währung", currency);
        AddField(form, "Log-Level", logLevel); AddField(form, "Logdatei", logFile); AddField(form, "Zahlungstimeout (s)", timeout);
        AddField(form, "SumUp API-Key", apiKey); AddField(form, "Merchant Code", merchantCode); AddField(form, "Terminal-/Reader-ID", terminalId); AddField(form, "Affiliate Key", affiliateKey); AddField(form, "Affiliate App-ID", affiliateApp);
        AddField(form, "Kassenschnittquelle", endOfDaySource); resetAfterReconciliation.Text = "Offene Posten nach erfolgreichem Kassenschnitt schließen"; resetAfterReconciliation.ForeColor = Theme.Muted; form.Controls.Add(resetAfterReconciliation, 1, form.RowCount++);
        AddField(form, "Update-Repository", updateRepository);
        revealSecrets.Text = "Secrets kurz anzeigen"; revealSecrets.ForeColor = Theme.Muted;
        revealSecrets.CheckedChanged += (_, _) =>
        {
            apiKey.UseSystemPasswordChar = affiliateKey.UseSystemPasswordChar = !revealSecrets.Checked;
            if (revealSecrets.Checked) secretRevealTimer.Start(); else secretRevealTimer.Stop();
        };
        form.Controls.Add(revealSecrets, 1, form.RowCount++);
        warning.AutoSize = true; warning.ForeColor = Theme.Amber; form.Controls.Add(warning, 1, form.RowCount++);
        FlowLayoutPanel actions = Row(); Button save = Theme.Button("Sicher speichern", 140, true), test = Theme.Button("SumUp testen", 130);
        save.Click += async (_, _) => await UiAsync(SaveOptionsAsync); test.Click += async (_, _) => await UiAsync(TestSumUpAsync);
        actions.Controls.Add(save); actions.Controls.Add(test); form.Controls.Add(actions, 1, form.RowCount++); return form;
    }
    private Panel BuildTerminals()
    {
        terminalList.Columns.Add("Name", 230); terminalList.Columns.Add("ID", 290); terminalList.Columns.Add("Status", 110); terminalList.Columns.Add("Seriennummer", 150);
        terminalList.DoubleClick += async (_, _) => await UiAsync(async () =>
        {
            if (terminalList.SelectedItems.Count == 0) return; terminalId.Text = terminalList.SelectedItems[0].Tag?.ToString() ?? string.Empty; await SaveOptionsAsync();
        });
        Panel panel = ContentPanel(); panel.Controls.Add(terminalList); FlowLayoutPanel row = Row(); row.Dock = DockStyle.Top; row.Height = 45;
        TextBox pairing = Input(true); pairing.Width = 130; pairing.PlaceholderText = "Pairing-Code"; Button load = Theme.Button("Neu laden"), pair = Theme.Button("Solo koppeln", 130, true);
        load.Click += async (_, _) => await UiAsync(LoadTerminalsAsync); pair.Click += async (_, _) => await UiAsync(async () => { await using ApiSession session = await ApiSession.CreateAsync(); TerminalDescriptor t = await session.Client.PairReaderAsync(pairing.Text, "ZVT2SumUp Terminal", CancellationToken.None); terminalId.Text = t.Id; pairing.Clear(); await SaveOptionsAsync(); await LoadTerminalsAsync(); });
        row.Controls.Add(pairing); row.Controls.Add(load); row.Controls.Add(pair); panel.Controls.Add(row); return panel;
    }
    private Panel BuildTransactions()
    {
        transactionList.Columns.Add("Status", 100); transactionList.Columns.Add("Betrag", 100); transactionList.Columns.Add("Währung", 80); transactionList.Columns.Add("Transaktion", 290); transactionList.Columns.Add("Kartentyp", 130);
        Panel panel = ContentPanel(); panel.Controls.Add(transactionList); FlowLayoutPanel row = Row(); row.Dock = DockStyle.Top; row.Height = 45;
        Button refresh = Theme.Button("Transaktionen laden", 170), full = Theme.Button("Voll erstatten", 140), partial = Theme.Button("Teilbetrag erstatten", 165);
        refresh.Click += async (_, _) => await UiAsync(LoadTransactionsAsync); full.Click += async (_, _) => await UiAsync(() => RefundSelectedAsync(false)); partial.Click += async (_, _) => await UiAsync(() => RefundSelectedAsync(true));
        row.Controls.Add(refresh); row.Controls.Add(full); row.Controls.Add(partial); panel.Controls.Add(row); return panel;
    }
    private SplitContainer BuildReceipts()
    {
        SplitContainer split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 510, BackColor = Theme.Border, Padding = new Padding(15) };
        foreach (RichTextBox box in new[] { receiptEditor, receiptPreview }) { box.Dock = DockStyle.Fill; box.Font = Theme.Mono; box.BackColor = Theme.DarkSurface; box.ForeColor = Theme.Ink; box.BorderStyle = BorderStyle.FixedSingle; }
        receiptPreview.ReadOnly = true; split.Panel1.Controls.Add(receiptEditor); split.Panel2.Controls.Add(receiptPreview);
        FlowLayoutPanel row = Row(); row.Dock = DockStyle.Bottom; row.Height = 44; Button load = Theme.Button("Laden"), preview = Theme.Button("Vorschau"), save = Theme.Button("Speichern", 120, true);
        load.Click += (_, _) => LoadReceiptFile(); preview.Click += (_, _) => PreviewReceipt(); save.Click += async (_, _) => await UiAsync(SaveReceiptFileAsync); row.Controls.Add(load); row.Controls.Add(preview); row.Controls.Add(save); split.Panel1.Controls.Add(row); LoadReceiptFile(); return split;
    }
    private RichTextBox BuildLog() { logBox.Dock = DockStyle.Fill; logBox.ReadOnly = true; logBox.BackColor = Theme.DarkSurface; logBox.ForeColor = Theme.Ink; logBox.Font = Theme.Mono; logBox.BorderStyle = BorderStyle.None; return logBox; }
    private FlowLayoutPanel BuildDiagnostics()
    {
        FlowLayoutPanel panel = new() { Dock = DockStyle.Fill, Padding = new Padding(25), BackColor = Theme.Background, FlowDirection = FlowDirection.TopDown };
        panel.Controls.Add(Theme.Heading("Sichere Diagnose")); Label text = new() { AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(700, 0), Text = "Diagnosen lösen keine reale Zahlung aus. Kassensimulator und APDU-Hexwerkzeuge befinden sich in ZVT2SumUp.Tools." }; panel.Controls.Add(text);
        Button sumup = Theme.Button("SumUp-Verbindung prüfen", 210, true), openTools = Theme.Button("Kassensimulator öffnen", 210);
        Button ports = Theme.Button("COM-Ports anzeigen", 190), simulator = Theme.Button("Gateway-Simulator", 190);
        sumup.Click += async (_, _) => await UiAsync(TestSumUpAsync); openTools.Click += (_, _) => StartTools("cash-register-simulator", false);
        ports.Click += (_, _) => StartTools("com-list", true); simulator.Click += (_, _) => StartTools("gateway-simulator", true);
        panel.Controls.Add(sumup); panel.Controls.Add(openTools); panel.Controls.Add(ports); panel.Controls.Add(simulator); return panel;
    }
    private FlowLayoutPanel BuildService()
    {
        FlowLayoutPanel panel = new() { Dock = DockStyle.Fill, Padding = new Padding(25), BackColor = Theme.Background, FlowDirection = FlowDirection.TopDown };
        panel.Controls.Add(Theme.Heading("Windows-Dienst")); panel.Controls.Add(new Label { Text = "Alle Änderungen erfordern eine ausdrücklich bestätigte UAC-Aktion.", AutoSize = true, ForeColor = Theme.Amber });
        foreach ((string title, Func<Task<ProcessResult>> action) in new (string, Func<Task<ProcessResult>>)[] { ("Dienst installieren", ServiceActions.InstallAsync), ("Dienst starten", ServiceActions.StartAsync), ("Dienst stoppen", ServiceActions.StopAsync), ("Dienst neu starten", ServiceActions.RestartAsync), ("Dienst deinstallieren", ServiceActions.UninstallAsync) })
        { Button button = Theme.Button(title, 190, title == "Dienst installieren"); button.Click += async (_, _) => await UiAsync(async () => { ProcessResult result = await action(); if (result.ExitCode != 0) throw new InvalidOperationException(result.Output); await RefreshServiceStatusAsync(); }); panel.Controls.Add(button); }
        return panel;
    }

    private async Task LoadOptionsAsync()
    {
        using IniOptionsStore optionsStore = new(AppPaths.Configuration);
        using DpapiSecretStore secretStore = new(AppPaths.Secrets);
        GatewayOptions o = await optionsStore.LoadAsync(); GatewaySecrets s = await secretStore.LoadAsync();
        mode.SelectedItem = o.Transport == GatewayTransport.Tcp ? "TCP" : "COM"; host.Text = o.TcpHost; port.Text = o.TcpPort.ToString(CultureInfo.InvariantCulture); idleTimeout.Text = o.TcpIdleTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        comPort.Text = o.ComPort; comBaud.Text = o.ComBaudRate.ToString(CultureInfo.InvariantCulture); currency.Text = o.Currency; logLevel.SelectedItem = NormalizeLogLevel(o.LogLevel); logFile.Text = o.LogFile;
        timeout.Text = o.PaymentTimeoutSeconds.ToString(CultureInfo.InvariantCulture); merchantCode.Text = o.MerchantCode; terminalId.Text = o.TerminalId; endOfDaySource.SelectedItem = o.EndOfDaySource;
        resetAfterReconciliation.Checked = o.ResetAfterReconciliation; updateRepository.Text = o.UpdateRepository;
        apiKey.Text = s.ApiKey; affiliateKey.Text = s.AffiliateKey; affiliateApp.Text = s.AffiliateAppId; warning.Text = o.IsExternallyBound ? "WARNUNG: Externe Bind-Adresse vergrößert die Angriffsfläche." : "Lokale Bindung 127.0.0.1 ist aktiv.";
        endpointStatus.Text = o.Transport == GatewayTransport.Tcp ? $"{o.TcpHost}:{o.TcpPort}" : $"{o.ComPort} / {o.ComBaudRate}";
        selectedTerminalStatus.Text = string.IsNullOrWhiteSpace(o.TerminalId) ? "NICHT AUSGEWÄHLT" : SensitiveDataRedactor.Mask(o.TerminalId); selectedTerminalStatus.ForeColor = string.IsNullOrWhiteSpace(o.TerminalId) ? Theme.Muted : Theme.BlueBright;
        await RefreshOverviewAsync();
    }
    private async Task SaveOptionsAsync()
    {
        if (!int.TryParse(port.Text, out int tcpPort) || !int.TryParse(idleTimeout.Text, out int tcpIdleTimeout) || !int.TryParse(comBaud.Text, out int baudRate) || !int.TryParse(timeout.Text, out int paymentTimeout))
            throw new InvalidDataException("Port, Idle-Timeout, Baudrate und Zahlungstimeout müssen ganze Zahlen sein.");
        using IniOptionsStore optionsStore = new(AppPaths.Configuration);
        using DpapiSecretStore secretStore = new(AppPaths.Secrets);
        GatewayOptions previous = await optionsStore.LoadAsync(); GatewayOptions options = previous with
        {
            Transport = mode.Text == "COM" ? GatewayTransport.Com : GatewayTransport.Tcp,
            TcpHost = host.Text.Trim(),
            TcpPort = tcpPort,
            TcpIdleTimeoutSeconds = tcpIdleTimeout,
            ComPort = comPort.Text.Trim(),
            ComBaudRate = baudRate,
            Currency = currency.Text.Trim().ToUpperInvariant(),
            LogLevel = logLevel.Text,
            LogFile = logFile.Text.Trim(),
            PaymentTimeoutSeconds = paymentTimeout,
            MerchantCode = merchantCode.Text.Trim(),
            TerminalId = terminalId.Text.Trim(),
            EndOfDaySource = endOfDaySource.Text,
            ResetAfterReconciliation = resetAfterReconciliation.Checked,
            UpdateRepository = updateRepository.Text.Trim()
        };
        IReadOnlyList<string> errors = options.Validate(false); if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        await optionsStore.SaveAsync(options); await secretStore.SaveAsync(new(apiKey.Text.Trim(), affiliateKey.Text.Trim(), affiliateApp.Text.Trim()));
        warning.Text = options.IsExternallyBound ? "WARNUNG: Externe Bind-Adresse vergrößert die Angriffsfläche." : "Sicher gespeichert. Lokale Bindung ist aktiv.";
    }
    private async Task TestSumUpAsync() { await SaveOptionsAsync(); await using ApiSession session = await ApiSession.CreateAsync(); ConnectionResult result = await session.Client.TestConnectionAsync(CancellationToken.None); SetStatus(sumUpStatus, sidebarSumUpStatus, result.Success ? "VERBUNDEN" : "FEHLER", result.Success ? Theme.Green : Theme.Danger); if (!result.Success) throw new InvalidOperationException(result.Error); merchantCode.Text = result.MerchantCode; await SaveOptionsAsync(); }
    private async Task LoadTerminalsAsync() { await using ApiSession session = await ApiSession.CreateAsync(); IReadOnlyList<TerminalDescriptor> items = await session.Client.GetTerminalsAsync(CancellationToken.None); terminalList.Items.Clear(); foreach (TerminalDescriptor t in items) { ListViewItem item = new(t.Name) { Tag = t.Id }; item.SubItems.Add(t.Id); item.SubItems.Add(t.Status); item.SubItems.Add(t.SerialNumber); terminalList.Items.Add(item); } }
    private async Task LoadTransactionsAsync() { await using ApiSession session = await ApiSession.CreateAsync(); IReadOnlyList<CheckoutResult> items = await session.Client.GetTransactionsAsync(50, CancellationToken.None); transactionList.Items.Clear(); foreach (CheckoutResult t in items) { ListViewItem item = new(t.Status) { Tag = t }; item.SubItems.Add(Money.Format(t.AmountCents)); item.SubItems.Add(t.Currency); item.SubItems.Add(t.TransactionId); item.SubItems.Add(t.CardType); transactionList.Items.Add(item); } }
    private async Task RefundSelectedAsync(bool partial)
    {
        if (transactionList.SelectedItems.Count != 1 || transactionList.SelectedItems[0].Tag is not CheckoutResult transaction)
            throw new InvalidOperationException("Bitte genau eine Transaktion auswählen.");
        if (transaction.Status != "PAID") throw new InvalidOperationException("Nur erfolgreich bezahlte Transaktionen können erstattet werden.");
        if (string.IsNullOrWhiteSpace(transaction.TransactionId)) throw new InvalidOperationException("Die Transaktions-ID fehlt.");

        long? requested = partial ? PromptRefundAmount(transaction.AmountCents) : null;
        if (partial && !requested.HasValue) return;
        long journalAmount = requested ?? transaction.AmountCents;
        if (journalAmount <= 0) throw new InvalidOperationException("Der Transaktionsbetrag konnte nicht sicher bestimmt werden.");
        if (transaction.AmountCents > 0 && journalAmount > transaction.AmountCents) throw new InvalidOperationException("Der Erstattungsbetrag ist größer als der ursprüngliche Betrag.");

        string label = partial ? $"{Money.Format(journalAmount)} {transaction.Currency}" : $"den vollständigen Betrag {Money.Format(journalAmount)} {transaction.Currency}";
        DialogResult confirmation = MessageBox.Show(this, $"Soll {label} wirklich erstattet werden?\n\nTransaktion: {transaction.TransactionId}",
            "Rückerstattung bestätigen", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirmation != DialogResult.Yes) return;

        await using ApiSession session = await ApiSession.CreateAsync();
        CheckoutResult result = await session.Client.RefundAsync(transaction.TransactionId, requested, CancellationToken.None);
        ITransactionJournal journal = session.Host.Services.GetRequiredService<ITransactionJournal>();
        await journal.AddRefundAsync(new TransactionRecord
        {
            TerminalId = terminalId.Text.Trim(),
            AmountCents = journalAmount,
            Currency = string.IsNullOrWhiteSpace(transaction.Currency) ? currency.Text.Trim().ToUpperInvariant() : transaction.Currency,
            TransactionId = transaction.TransactionId,
            CheckoutId = result.Id,
            Status = "REFUNDED"
        });
        MessageBox.Show(this, $"{Money.Format(journalAmount)} {transaction.Currency} wurden erfolgreich erstattet.", "Rückerstattung", MessageBoxButtons.OK, MessageBoxIcon.Information);
        await LoadTransactionsAsync();
    }

    private long? PromptRefundAmount(long maximumCents)
    {
        using Form dialog = new()
        {
            Text = "Teilbetrag erstatten",
            ClientSize = new Size(390, 155),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.Background,
            ForeColor = Theme.Ink,
            Font = Theme.Body
        };
        Label prompt = new() { Text = maximumCents > 0 ? $"Betrag in EUR (maximal {Money.Format(maximumCents)}):" : "Betrag in EUR:", AutoSize = true, Location = new Point(20, 20), ForeColor = Theme.Muted };
        TextBox value = Input(); value.Location = new Point(20, 50); value.Width = 345; value.Text = maximumCents > 0 ? Money.Format(maximumCents) : string.Empty;
        Button cancel = Theme.Button("Abbrechen", 105), confirm = Theme.Button("Erstatten", 105, true); cancel.Location = new Point(140, 100); confirm.Location = new Point(260, 100);
        cancel.DialogResult = DialogResult.Cancel; confirm.DialogResult = DialogResult.OK; dialog.CancelButton = cancel; dialog.AcceptButton = confirm; dialog.Controls.AddRange([prompt, value, cancel, confirm]); Theme.DarkTitleBar(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        if (!decimal.TryParse(value.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.GetCultureInfo("de-DE"), out decimal amount) &&
            !decimal.TryParse(value.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out amount))
            throw new InvalidDataException("Der Erstattungsbetrag ist ungültig.");
        long cents = Money.ToMinor(amount); if (cents <= 0) throw new InvalidDataException("Der Erstattungsbetrag muss größer als 0 sein."); return cents;
    }

    private async Task StartGatewayAsync() { await SaveOptionsAsync(); await runtime.StartAsync(); SetStatus(gatewayStatus, sidebarGatewayStatus, "LÄUFT", Theme.Green); }
    private async Task StopGatewayAsync() { await runtime.StopAsync(); SetStatus(gatewayStatus, sidebarGatewayStatus, "GESTOPPT", Theme.Muted); }
    private async Task RefreshServiceStatusAsync() { string value = (await ServiceActions.StatusAsync()).ToUpperInvariant(); SetStatus(serviceStatus, sidebarServiceStatus, value, value == "LÄUFT" ? Theme.Green : Theme.Muted); }
    private async Task CheckUpdatesAsync()
    {
        await using ApiSession session = await ApiSession.CreateAsync(); IUpdateService updates = session.Host.Services.GetRequiredService<IUpdateService>(); UpdateInformation info = await updates.CheckAsync();
        if (!string.IsNullOrEmpty(info.Error)) throw new InvalidOperationException(info.Error); if (!info.Available) { MessageBox.Show(this, "ZVT2SumUp ist aktuell.", "Updates"); return; }
        DialogResult answer = MessageBox.Show(this,
            $"Version {info.RemoteVersion} ist verfügbar.\n\nDas Update wird ausschließlich von GitHub geladen, gegen checksums.sha256 geprüft, vor dem Austausch getestet und bei einem Fehler zurückgerollt. Gateway und gegebenenfalls Dienst werden sicher beendet; danach startet die aktualisierte Oberfläche.\n\nJetzt mit UAC-Bestätigung installieren?",
            "Sicheres GitHub-Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        string tools = Path.Combine(AppContext.BaseDirectory, SecureReleaseUpdateService.ToolsExecutableName);
        if (!File.Exists(tools)) throw new FileNotFoundException("Für das automatische Update muss ZVT2SumUp.Tools.exe neben der Gateway-EXE liegen.", tools);
        ProcessStartInfo start = new(tools) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        foreach (string argument in new[]
        {
            "update", "--confirm-update", "--expected-version", $"{info.RemoteVersion!.Major}.{info.RemoteVersion.Minor}.{Math.Max(0, info.RemoteVersion.Build)}",
            "--wait-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }) start.ArgumentList.Add(argument);
        using Process updater = Process.Start(start) ?? throw new InvalidOperationException("Updateprozess konnte nicht gestartet werden.");
        BeginInvoke(Close);
    }
    private void LoadReceiptFile() { if (File.Exists(AppPaths.ReceiptTemplates)) receiptEditor.Text = File.ReadAllText(AppPaths.ReceiptTemplates); }
    private async Task SaveReceiptFileAsync() { await File.WriteAllTextAsync(AppPaths.ReceiptTemplates, receiptEditor.Text, Encoding.UTF8); PreviewReceipt(); }
    private void PreviewReceipt() { IniDocument ini = IniDocument.Parse(receiptEditor.Text); receiptPreview.Text = ini.Get("payment", "lines").Replace("\\n", Environment.NewLine, StringComparison.Ordinal); }
    private void RefreshLogs()
    {
        try
        {
            string? file = Directory.Exists(AppPaths.Logs) ? Directory.GetFiles(AppPaths.Logs, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null; if (file is null) return;
            string text; using (FileStream s = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { if (s.Length > 200_000) s.Seek(-200_000, SeekOrigin.End); using StreamReader r = new(s); text = r.ReadToEnd(); }
            if (lastLogText == text) return; lastLogText = text; logBox.SuspendLayout(); logBox.Clear();
            foreach (string line in text.Split('\n'))
            {
                int start = logBox.TextLength; logBox.AppendText(line.TrimEnd('\r') + Environment.NewLine); logBox.Select(start, logBox.TextLength - start);
                logBox.SelectionColor = LogColor(line);
            }
            logBox.Select(logBox.TextLength, 0); logBox.SelectionColor = Theme.Ink; logBox.ScrollToCaret(); logBox.ResumeLayout();
            int connected = text.LastIndexOf("Kassensystem verbunden", StringComparison.OrdinalIgnoreCase), disconnected = text.LastIndexOf("Kassensystem getrennt", StringComparison.OrdinalIgnoreCase);
            bool active = connected >= 0 && connected > disconnected; cashRegisterStatus.Text = active ? "VERBUNDEN" : "GETRENNT"; cashRegisterStatus.ForeColor = active ? Theme.Green : Theme.Muted;
        }
        catch (IOException) { }
    }

    private async Task RefreshOverviewAsync()
    {
        try
        {
            using IniOptionsStore optionsStore = new(AppPaths.Configuration);
            using JsonTransactionJournal journal = new(AppPaths.Journal);
            GatewayOptions options = await optionsStore.LoadAsync();
            IReadOnlyList<TransactionRecord> open = await journal.GetOpenAsync(string.IsNullOrWhiteSpace(options.TerminalId) ? null : options.TerminalId);
            JournalSummary summary = JournalSummary.From(open); journalStatus.Text = $"{summary.TransactionCount} OFFEN"; journalStatus.ForeColor = summary.TransactionCount == 0 ? Theme.Green : Theme.Amber;
            TransactionRecord? latest = open.OrderByDescending(item => item.Timestamp).FirstOrDefault(); lastPaymentStatus.Text = latest is null ? "NOCH KEINE" : $"{Money.Format(Math.Abs(latest.AmountCents))} {latest.Currency}";
            lastPaymentStatus.ForeColor = latest is null ? Theme.Muted : latest.Type.Equals("REFUND", StringComparison.OrdinalIgnoreCase) ? Theme.Amber : Theme.Green;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { journalStatus.Text = "FEHLER"; journalStatus.ForeColor = Theme.Danger; }
    }
    private async Task UiAsync(Func<Task> action) { try { UseWaitCursor = true; await action(); } catch (Exception ex) { MessageBox.Show(this, SensitiveDataRedactor.Redact(ex.Message), "ZVT2SumUp", MessageBoxButtons.OK, MessageBoxIcon.Error); } finally { UseWaitCursor = false; } }
    private async void FormIsClosing(object? sender, FormClosingEventArgs e) { if (closing) return; e.Cancel = true; closing = true; logTimer.Stop(); await runtime.DisposeAsync(); Close(); }
    protected override void Dispose(bool disposing) { if (disposing) { logTimer.Dispose(); secretRevealTimer.Dispose(); } base.Dispose(disposing); }
    private static void ShowSafety() => MessageBox.Show("Secrets werden mit Windows DPAPI (LocalMachine) verschlüsselt und über ACLs geschützt. Authorization-Header, API-Keys und Pairing-Codes werden redigiert. Standardmäßig lauscht das Gateway ausschließlich auf 127.0.0.1. Geldbeträge werden nur als Integer-Cent bzw. decimal verarbeitet.", "Über & Sicherheit", MessageBoxButtons.OK, MessageBoxIcon.Information);
    private static void Open(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private static Icon? LoadIcon()
    {
        try { string asset = Path.Combine(AppContext.BaseDirectory, "assets", "zvt2sumup.ico"); return File.Exists(asset) ? new Icon(asset) : Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return null; }
    }
    private void StartTools(string command, bool wait)
    {
        string? exe = FindToolsExecutable();
        if (exe is null)
        {
            MessageBox.Show(this,
                "ZVT2SumUp.Tools.exe wurde nicht gefunden. Das Installationspaket ist unvollständig.",
                "Tools nicht gefunden", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ProcessStartInfo start = new(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        start.ArgumentList.Add(command);
        if (wait) start.ArgumentList.Add("--wait");
        Process.Start(start);
    }

    private static string? FindToolsExecutable()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "ZVT2SumUp.Tools.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ZVT2SumUp.Tools.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Zvt2SumUp.Tools", "bin", "Debug", "net10.0-windows", "win-x64", "ZVT2SumUp.Tools.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Zvt2SumUp.Tools", "bin", "Release", "net10.0-windows", "win-x64", "ZVT2SumUp.Tools.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Zvt2SumUp.Tools", "bin", "Debug", "net10.0-windows", "ZVT2SumUp.Tools.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Zvt2SumUp.Tools", "bin", "Release", "net10.0-windows", "ZVT2SumUp.Tools.exe"))
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static TabPage Page(string title, Control content) { TabPage page = new(title) { BackColor = Theme.Background, Padding = new Padding(4) }; content.Dock = DockStyle.Fill; page.Controls.Add(content); return page; }
    private static Label Section(string text) => new() { Text = text, ForeColor = Theme.BlueBright, Font = Theme.Mono, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft };
    private static void SetStatus(Label first, Label second, string text, Color color) { first.Text = second.Text = text; first.ForeColor = second.ForeColor = color; }
    private static string Shorten(string value, int maximum) => value.Length <= maximum ? value : value[..Math.Max(1, maximum - 1)] + "…";
    private static Color LogColor(string line)
    {
        if (line.Contains("[Error", StringComparison.OrdinalIgnoreCase) || line.Contains("[Critical", StringComparison.OrdinalIgnoreCase) || line.Contains("FEHLER", StringComparison.OrdinalIgnoreCase)) return Theme.Danger;
        if (line.Contains("[Warning", StringComparison.OrdinalIgnoreCase) || line.Contains("WARN", StringComparison.OrdinalIgnoreCase)) return Theme.Amber;
        if (line.Contains("erfolgreich", StringComparison.OrdinalIgnoreCase) || line.Contains("PAID", StringComparison.OrdinalIgnoreCase) || line.Contains(" OK", StringComparison.OrdinalIgnoreCase)) return Theme.Green;
        if (line.Contains("ZVT", StringComparison.OrdinalIgnoreCase) || line.Contains("TCP", StringComparison.OrdinalIgnoreCase) || line.Contains("HTTP", StringComparison.OrdinalIgnoreCase)) return Theme.BlueBright;
        return Theme.Ink;
    }
    private static string NormalizeLogLevel(string value) => value.ToUpperInvariant() switch
    { "TRACE" => "Trace", "DEBUG" => "Debug", "WARNING" or "WARN" => "Warning", "ERROR" => "Error", "CRITICAL" or "FATAL" => "Critical", _ => "Information" };
    private static Label StatusLabel(string text, Color color) => new() { Text = text, ForeColor = color, Font = new("Consolas", 10, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private static FlowLayoutPanel Row() => new() { BackColor = Theme.Sidebar, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Dock = DockStyle.Fill };
    private static TableLayoutPanel ButtonPair(Button first, Button second)
    {
        TableLayoutPanel pair = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
        pair.ColumnStyles.Add(new(SizeType.Percent, 50)); pair.ColumnStyles.Add(new(SizeType.Percent, 50));
        first.Dock = DockStyle.Fill; second.Dock = DockStyle.Fill; pair.Controls.Add(first, 0, 0); pair.Controls.Add(second, 1, 0); return pair;
    }
    private void UpdateResponsiveShell() { if (sidebar is not null) sidebar.Width = ClientSize.Width < 1100 ? 248 : 320; }
    private static TextBox Input(bool secret = false) { TextBox value = new() { Width = 330, UseSystemPasswordChar = secret }; Theme.Input(value); return value; }
    private static ListView List() { ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BackColor = Theme.DarkSurface, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle }; return list; }
    private static Panel ContentPanel() => new() { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(16) };
    private static Panel Card(string heading, Control value, string detail) { Panel card = new() { Width = 260, Height = 120, BackColor = Theme.Surface, Margin = new Padding(8), Padding = new Padding(14) }; Label title = new() { Text = heading, ForeColor = Theme.BlueBright, Font = Theme.Mono, Dock = DockStyle.Top, Height = 25 }; value.Dock = DockStyle.Top; value.Height = 30; Label info = new() { Text = detail, ForeColor = Theme.Muted, Dock = DockStyle.Bottom, Height = 28 }; card.Controls.Add(info); card.Controls.Add(value); card.Controls.Add(title); return card; }
    internal string? ValidateMinimumLayout()
    {
        Size = new Size(960, 640);
        PerformLayout();
        tabs.PerformLayout();
        for (int index = 0; index < tabs.TabCount; index++)
        {
            Rectangle bounds = tabs.GetTabRect(index);
            int textWidth = TextRenderer.MeasureText(tabs.TabPages[index].Text, tabs.Font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
            if (bounds.Left < 0 || bounds.Right > tabs.ClientSize.Width || textWidth > bounds.Width)
                return $"Tab '{tabs.TabPages[index].Text}' ist bei 960 x 640 nicht vollständig sichtbar " +
                    $"(Tab={bounds.Left}..{bounds.Right}, Fläche={tabs.ClientSize.Width}, Text={textWidth}, Breite={bounds.Width}).";
        }

        tabs.SelectedIndex = 0;
        tabs.SelectedTab?.PerformLayout();
        if (tabs.SelectedTab?.Controls.OfType<ResponsiveCardPanel>().SingleOrDefault() is not ResponsiveCardPanel cards)
            return "Responsive Übersicht wurde nicht gefunden.";
        cards.PerformLayout();
        int visibleRight = cards.ClientSize.Width - cards.Padding.Right;
        foreach (Control card in cards.Controls)
            if (card.Left < cards.Padding.Left || card.Right > visibleRight)
                return "Eine Übersichtskarte liegt bei 960 x 640 außerhalb des sichtbaren Bereichs.";
        return null;
    }
    private static TableLayoutPanel FormLayout() { TableLayoutPanel form = new() { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(25), BackColor = Theme.Background }; form.ColumnStyles.Add(new(SizeType.Absolute, 190)); form.ColumnStyles.Add(new(SizeType.Percent, 100)); return form; }
    private static void AddField(TableLayoutPanel form, string label, Control input) { int row = form.RowCount++; form.RowStyles.Add(new(SizeType.Absolute, 42)); form.Controls.Add(new Label { Text = label, ForeColor = Theme.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row); form.Controls.Add(input, 1, row); }
}

internal sealed class ApiSession : IAsyncDisposable
{
    public Microsoft.Extensions.Hosting.IHost Host { get; }
    public ISumUpClient Client { get; }
    private ApiSession(Microsoft.Extensions.Hosting.IHost host, ISumUpClient client) { Host = host; Client = client; }
    public static async Task<ApiSession> CreateAsync() { var session = await RuntimeHostController.CreateApiSessionAsync(); return new(session.Host, session.Client); }
    public async ValueTask DisposeAsync() { await Host.StopAsync(); Host.Dispose(); }
}
