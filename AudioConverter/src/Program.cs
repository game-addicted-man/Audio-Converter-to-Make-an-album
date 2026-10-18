using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Audio Converter")]
[assembly: System.Reflection.AssemblyDescription("MP3 320 kbps / FLAC 44.1 kHz batch converter")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace AudioConverter
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            { MessageBox.Show(e.Exception.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error); };
            using (var form = new MainForm())
            {
                if (args.Length == 2 && args[0] == "--render-preview")
                {
                    form.Show();
                    Application.DoEvents();
                    File.WriteAllText(Path.ChangeExtension(args[1], ".layout.txt"), Describe(form, ""));
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Close();
                    return;
                }
                if (args.Length > 0) form.Shown += async delegate { await form.AddPathsAsync(args); };
                Application.Run(form);
            }
        }

        private static string Describe(Control control, string indent)
        {
            string text = indent + control.GetType().Name + " [" + control.Text.Replace("\n", " ") + "] " + control.Bounds + " Visible=" + control.Visible + "\r\n";
            foreach (Control child in control.Controls) text += Describe(child, indent + "  ");
            return text;
        }
    }

    internal sealed class MainForm : Form
    {
        internal static readonly Color Background = Color.FromArgb(17, 21, 28);
        internal static readonly Color Surface = Color.FromArgb(26, 32, 42);
        internal static readonly Color Muted = Color.FromArgb(151, 165, 184);
        internal static readonly Color Accent = Color.FromArgb(108, 231, 204);
        private readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".wave", ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".aif", ".aiff", ".wma", ".alac", ".ape", ".wv", ".ac3", ".dts", ".mp2", ".m4b", ".caf", ".dsf", ".dff" };
        private readonly List<string> errors = new List<string>();
        private CheckBox mp3, flac;
        private TextBox output;
        private Button browse, addFiles, addFolder, remove, clear, start, cancel, showErrors, openOutput;
        private Label count, status, percent, placeholder;
        private ListView queue;
        private Meter meter;
        private CancellationTokenSource cancellation;
        private bool running, scanning, closeWhenDone;

        public MainForm()
        {
            Text = "Audio Converter · 음악 포맷 변환기";
            Font = new Font("Malgun Gothic", 10F);
            BackColor = Background;
            ForeColor = Color.FromArgb(237, 243, 250);
            ClientSize = new Size(980, 748);
            MinimumSize = new Size(920, 740);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BuildLayout();
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (running)
                {
                    e.Cancel = true;
                    closeWhenDone = true;
                    RequestCancel();
                }
                else if (scanning)
                {
                    e.Cancel = true;
                    closeWhenDone = true;
                    RequestCancel();
                }
            };
        }

        private Label LabelAt(string text, int size, Color color)
        {
            return new Label { Text = text, Font = new Font("Malgun Gothic", size), ForeColor = color,
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
        }

        private Button ButtonFor(string text, int width, EventHandler action, bool primary)
        {
            var button = new Button { Text = text, Width = width, Height = 36, FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand, BackColor = primary ? Accent : Surface,
                ForeColor = primary ? Background : ForeColor, Margin = new Padding(0, 0, 8, 0),
                UseVisualStyleBackColor = false, TabStop = true };
            button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(61, 73, 89);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(146, 242, 222) : Color.FromArgb(39, 49, 64);
            button.Click += action;
            return button;
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 20),
                ColumnCount = 1, RowCount = 9, BackColor = Background };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] { 82, 24, 90, 78, 46 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            Controls.Add(root);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            var mark = new WaveMark { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 9) };
            header.Controls.Add(mark, 0, 0);
            header.SetRowSpan(mark, 2);
            var title = LabelAt("음악 포맷 변환기", 22, ForeColor);
            title.Font = new Font("Malgun Gothic", 22, FontStyle.Bold);
            header.Controls.Add(title, 1, 0);
            header.Controls.Add(LabelAt("원하는 형식으로, 한 번에.   MP3 / FLAC AUDIO CONVERTER", 10, Muted), 1, 1);
            root.Controls.Add(header, 0, 0);
            root.Controls.Add(LabelAt("01   출력 형식  ·  두 가지를 함께 선택할 수 있어요", 10, Muted), 0, 1);

            var formats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 4, 0, 12) };
            formats.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            formats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            formats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            formats.Controls.Add(FormatCard("MP3", "320 kbps  ·  44.1 kHz  ·  CBR", out mp3), 0, 0);
            formats.Controls.Add(FormatCard("FLAC", "44.1 kHz  ·  16 / 24-bit 자동", out flac), 1, 0);
            root.Controls.Add(formats, 0, 2);

            var destination = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 10) };
            destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            destination.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            destination.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            destination.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            var destLabel = LabelAt("02   저장 위치", 10, Muted);
            destination.Controls.Add(destLabel, 0, 0);
            destination.SetColumnSpan(destLabel, 2);
            string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrEmpty(music)) music = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            output = new TextBox { Text = Path.Combine(music, "AudioConverter"), Dock = DockStyle.Fill,
                BackColor = Surface, ForeColor = ForeColor, BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 3, 10, 0), AccessibleName = "저장 폴더" };
            browse = ButtonFor("폴더 선택", 110, ChooseOutput, false);
            browse.Dock = DockStyle.Fill;
            browse.Margin = Padding.Empty;
            destination.Controls.Add(output, 0, 1);
            destination.Controls.Add(browse, 1, 1);
            root.Controls.Add(destination, 0, 3);

            var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 5, 0, 5) };
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 418));
            count = LabelAt("03   변환 목록  ·  0개", 10, Muted);
            toolbar.Controls.Add(count, 0, 0);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            addFiles = ButtonFor("파일 추가", 104, async delegate { await ChooseFiles(); }, false);
            addFolder = ButtonFor("폴더 추가", 104, async delegate { await ChooseFolder(); }, false);
            remove = ButtonFor("선택 삭제", 96, delegate { RemoveSelected(); }, false);
            clear = ButtonFor("비우기", 82, delegate { queue.Items.Clear(); known.Clear(); UpdateCount(); }, false);
            buttons.Controls.AddRange(new Control[] { addFiles, addFolder, remove, clear });
            toolbar.Controls.Add(buttons, 1, 0);
            root.Controls.Add(toolbar, 0, 4);

            var queuePanel = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = Padding.Empty, Padding = new Padding(1) };
            queue = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true,
                HideSelection = false, BackColor = Surface, ForeColor = ForeColor, BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, OwnerDraw = true, AllowDrop = true, AccessibleName = "변환할 음악 파일 목록" };
            queue.Columns.Add("파일 이름", 290);
            queue.Columns.Add("원본 폴더", 430);
            queue.Columns.Add("상태", 150);
            queue.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e)
            {
                using (var brush = new SolidBrush(Color.FromArgb(35, 43, 55))) e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, Rectangle.Inflate(e.Bounds, -12, 0), Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
            queue.DrawItem += delegate(object sender, DrawListViewItemEventArgs e) { };
            queue.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e)
            {
                bool selected = e.Item.Selected;
                using (var brush = new SolidBrush(selected ? Color.FromArgb(43, 65, 74) : Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
                Color color = e.ColumnIndex == 2 ? Accent : (e.ColumnIndex == 1 ? Muted : ForeColor);
                if (e.ColumnIndex == 2 && e.SubItem.Text.StartsWith("실패")) color = Color.FromArgb(255, 153, 153);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, Rectangle.Inflate(e.Bounds, -12, 0), color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (selected && e.ColumnIndex == 0) e.DrawFocusRectangle(e.Bounds);
            };
            queue.DragEnter += OnDragEnter;
            queue.DragDrop += OnDragDrop;
            queue.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete && !running && !scanning) RemoveSelected();
                if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem item in queue.Items) item.Selected = true;
            };
            queue.Resize += delegate
            {
                if (queue.ClientSize.Width > 500)
                {
                    queue.Columns[0].Width = (int)(queue.ClientSize.Width * 0.35);
                    queue.Columns[2].Width = 170;
                    queue.Columns[1].Width = Math.Max(140, queue.ClientSize.Width - queue.Columns[0].Width - 175);
                }
            };
            queuePanel.Controls.Add(queue);
            placeholder = new Label { Text = "음악 파일이나 폴더를 여기에 놓으세요\n\nWAV · MP3 · FLAC · M4A · AAC · OGG · OPUS · AIFF 등",
                ForeColor = Muted, BackColor = Surface, TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill, AllowDrop = true, Cursor = Cursors.Hand };
            placeholder.DragEnter += OnDragEnter;
            placeholder.DragDrop += OnDragDrop;
            placeholder.Click += async delegate { await ChooseFiles(); };
            queuePanel.Controls.Add(placeholder);
            placeholder.BringToFront();
            root.Controls.Add(queuePanel, 0, 5);

            root.Controls.Add(LabelAt("원본 보존 · 같은 이름은 번호 추가 · 태그 유지 (커버 이미지 제외)", 9, Muted), 0, 6);
            var progressRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            progressRow.RowCount = 1;
            progressRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            meter = new Meter { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 10, 8) };
            percent = LabelAt("0%", 9, Muted);
            percent.TextAlign = ContentAlignment.MiddleRight;
            progressRow.Controls.Add(meter, 0, 0);
            progressRow.Controls.Add(percent, 1, 0);
            root.Controls.Add(progressRow, 0, 7);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 12, 0, 0) };
            footer.RowCount = 1;
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 480));
            status = LabelAt("파일을 추가하면 시작할 수 있어요.", 10, Muted);
            footer.Controls.Add(status, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            start = ButtonFor("변환 시작", 124, async delegate { await StartConversion(); }, true);
            cancel = ButtonFor("취소", 70, delegate { RequestCancel(); }, false);
            cancel.Enabled = false;
            openOutput = ButtonFor("저장 폴더 열기", 130, delegate { OpenOutputFolder(); }, false);
            showErrors = ButtonFor("오류 보기", 96, delegate { ShowErrors(); }, false);
            showErrors.Visible = false;
            actions.Controls.AddRange(new Control[] { start, cancel, openOutput, showErrors });
            footer.Controls.Add(actions, 1, 0);
            root.Controls.Add(footer, 0, 8);
            UpdateCount();
        }

        private Control FormatCard(string title, string description, out CheckBox check)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = Surface, Margin = new Padding(title == "MP3" ? 0 : 6, 0, title == "MP3" ? 6 : 0, 0), Padding = new Padding(18, 6, 12, 8) };
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            check = new CheckBox { Text = "  " + title, Checked = true, Dock = DockStyle.Fill,
                ForeColor = Accent, Font = new Font("Segoe UI", 13, FontStyle.Bold), Cursor = Cursors.Hand,
                Margin = Padding.Empty, AccessibleName = title + " 출력" };
            panel.Controls.Add(check, 0, 0);
            panel.Controls.Add(LabelAt(description, 10, Muted), 0, 1);
            return panel;
        }

        private async Task ChooseFiles()
        {
            if (running || scanning) return;
            using (var dialog = new OpenFileDialog { Title = "변환할 음악 선택", Multiselect = true,
                Filter = "음악 파일|*.wav;*.wave;*.mp3;*.flac;*.m4a;*.aac;*.ogg;*.oga;*.opus;*.aif;*.aiff;*.wma;*.alac;*.ape;*.wv;*.ac3;*.dts;*.mp2;*.m4b;*.caf;*.dsf;*.dff|모든 파일|*.*" })
                if (dialog.ShowDialog(this) == DialogResult.OK) await AddPathsAsync(dialog.FileNames);
        }

        private async Task ChooseFolder()
        {
            if (running || scanning) return;
            using (var dialog = new FolderBrowserDialog { Description = "음악 폴더 선택 (하위 폴더 포함)" })
                if (dialog.ShowDialog(this) == DialogResult.OK) await AddPathsAsync(new[] { dialog.SelectedPath });
        }

        private void ChooseOutput(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "변환한 파일을 저장할 폴더", SelectedPath = output.Text })
                if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.SelectedPath;
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        { e.Effect = !running && !scanning && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; }

        private async void OnDragDrop(object sender, DragEventArgs e)
        {
            if (!running && !scanning && e.Data.GetDataPresent(DataFormats.FileDrop))
                await AddPathsAsync((string[])e.Data.GetData(DataFormats.FileDrop));
        }

        public async Task AddPathsAsync(IEnumerable<string> paths)
        {
            if (running || scanning) return;
            scanning = true;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            SetBusy(true);
            status.Text = "음악 파일을 찾고 있어요…";
            var found = new List<string>();
            int inaccessible = 0;
            try
            {
                await Task.Run(delegate
                {
                    var pending = new Stack<string>(paths.Reverse());
                    while (pending.Count > 0 && found.Count < 10000)
                    {
                        token.ThrowIfCancellationRequested();
                        string path = pending.Pop();
                        try
                        {
                            if (Directory.Exists(path))
                            {
                                foreach (string file in Directory.EnumerateFiles(path))
                                {
                                    token.ThrowIfCancellationRequested();
                                    if (extensions.Contains(Path.GetExtension(file))) found.Add(Path.GetFullPath(file));
                                    if (found.Count >= 10000) break;
                                }
                                foreach (string folder in Directory.EnumerateDirectories(path).Reverse())
                                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) pending.Push(folder);
                            }
                            else if (File.Exists(path) && extensions.Contains(Path.GetExtension(path))) found.Add(Path.GetFullPath(path));
                        }
                        catch (Exception ex)
                        {
                            if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)) throw;
                            inaccessible++;
                        }
                    }
                });
                token.ThrowIfCancellationRequested();
                int added = 0;
                queue.BeginUpdate();
                try
                {
                    foreach (string path in found)
                    {
                        if (queue.Items.Count >= 10000) break;
                        if (!known.Add(path)) continue;
                        var item = new ListViewItem(new[] { Path.GetFileName(path), Path.GetDirectoryName(path), "대기" }) { Tag = path };
                        queue.Items.Add(item);
                        added++;
                    }
                }
                finally { queue.EndUpdate(); }
                UpdateCount();
                status.Text = string.Format("{0}개 추가 · 총 {1}개", added, queue.Items.Count);
                if (inaccessible > 0) status.Text += " · 일부 폴더 접근 불가";
                if (queue.Items.Count >= 10000) status.Text += " · 최대 10,000개";
            }
            catch (OperationCanceledException) { status.Text = "파일 추가가 취소되었습니다."; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "파일 추가 오류", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally
            {
                scanning = false;
                cancellation.Dispose();
                cancellation = null;
                SetBusy(false);
                if (closeWhenDone) Close();
            }
        }

        private void UpdateCount()
        {
            count.Text = string.Format("03   변환 목록  ·  {0}개", queue.Items.Count);
            placeholder.Visible = queue.Items.Count == 0;
            queue.Visible = queue.Items.Count > 0;
            if (placeholder.Visible) placeholder.BringToFront();
        }

        private void RemoveSelected()
        {
            if (running || scanning) return;
            foreach (ListViewItem item in queue.SelectedItems.Cast<ListViewItem>().ToArray())
            { known.Remove((string)item.Tag); queue.Items.Remove(item); }
            UpdateCount();
        }

        private void SetBusy(bool value)
        {
            foreach (Control control in new Control[] { mp3, flac, output, browse, addFiles, addFolder, remove, clear, start }) control.Enabled = !value;
            cancel.Enabled = running || scanning;
        }

        private void Post(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        private void SetProgress(double value)
        {
            value = Math.Max(0, Math.Min(1, value));
            meter.Value = value;
            percent.Text = ((int)(value * 100)).ToString() + "%";
        }

        private async Task StartConversion()
        {
            if (running || scanning) return;
            if (queue.Items.Count == 0) { await ChooseFiles(); return; }
            if (!mp3.Checked && !flac.Checked)
            { MessageBox.Show(this, "MP3 또는 FLAC을 선택하세요.", "출력 형식"); return; }
            string ffmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ffmpeg.exe");
            string ffprobe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ffprobe.exe");
            if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
            { MessageBox.Show(this, "tools 폴더의 ffmpeg.exe와 ffprobe.exe가 필요합니다.\n배포 ZIP을 모두 압축 해제한 후 실행하세요.", "변환 엔진을 찾을 수 없음", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            string destination;
            try
            {
                if (string.IsNullOrWhiteSpace(output.Text)) throw new IOException("저장 폴더를 선택하세요.");
                if (!Path.IsPathRooted(output.Text.Trim())) throw new IOException("저장 위치는 전체 폴더 경로로 입력하세요.");
                destination = Path.GetFullPath(output.Text.Trim());
                Directory.CreateDirectory(destination);
                string writeTest = Path.Combine(destination, ".audioconverter-write-" + Guid.NewGuid().ToString("N"));
                using (File.Create(writeTest)) { }
                File.Delete(writeTest);
                output.Text = destination;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "저장 폴더를 확인하세요", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

            var targets = new List<AudioTarget>();
            if (mp3.Checked) targets.Add(AudioTarget.Mp3);
            if (flac.Checked) targets.Add(AudioTarget.Flac);
            var items = queue.Items.Cast<ListViewItem>().ToArray();
            errors.Clear();
            showErrors.Visible = false;
            running = true;
            cancellation = new CancellationTokenSource();
            SetBusy(true);
            SetProgress(0);
            foreach (ListViewItem item in items) item.SubItems[2].Text = "대기";
            int succeeded = 0, failed = 0, completed = 0;
            int total = items.Length * targets.Count;
            bool wasCanceled = false;
            var token = cancellation.Token;
            try
            {
                var engine = new ConversionEngine(ffmpeg, ffprobe);
                await Task.Run(delegate
                {
                    foreach (ListViewItem item in items)
                    {
                        token.ThrowIfCancellationRequested();
                        string input = (string)item.Tag;
                        string fileName = Path.GetFileName(input);
                        Post(delegate { item.SubItems[2].Text = "분석 중"; status.Text = fileName + " 분석 중…"; });
                        AudioInfo info;
                        try { info = engine.Probe(input, token); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            failed += targets.Count;
                            completed += targets.Count;
                            errors.Add(fileName + "\r\n" + input + "\r\n" + ex.Message);
                            double fraction = (double)completed / total;
                            Post(delegate { item.SubItems[2].Text = "실패 · 오류 보기"; SetProgress(fraction); });
                            continue;
                        }
                        var doneFormats = new List<string>();
                        bool itemFailed = false;
                        foreach (AudioTarget target in targets)
                        {
                            token.ThrowIfCancellationRequested();
                            string format = target == AudioTarget.Mp3 ? "MP3" : "FLAC";
                            int before = completed;
                            Post(delegate { item.SubItems[2].Text = format + " 변환 중"; status.Text = string.Format("{0}/{1} · {2}", before + 1, total, fileName); });
                            try
                            {
                                string targetFolder = Path.Combine(destination, format);
                                Directory.CreateDirectory(targetFolder);
                                string finalPath = ConversionEngine.UniqueOutputPath(targetFolder, input, target);
                                engine.Convert(input, finalPath, target, info, delegate(double current)
                                { Post(delegate { SetProgress((before + current) / total); }); }, token);
                                succeeded++;
                                doneFormats.Add(format);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                failed++;
                                itemFailed = true;
                                errors.Add(fileName + " → " + format + "\r\n" + input + "\r\n" + ex.Message);
                            }
                            completed++;
                            string result = doneFormats.Count > 0 ? string.Join(" + ", doneFormats.ToArray()) + " 완료" : "실패";
                            if (itemFailed && doneFormats.Count > 0) result += " · 일부 실패";
                            double fraction = (double)completed / total;
                            Post(delegate { item.SubItems[2].Text = result; SetProgress(fraction); });
                        }
                    }
                }, token);
            }
            catch (OperationCanceledException) { wasCanceled = true; }
            catch (Exception ex) { errors.Add(ex.ToString()); failed++; }
            finally
            {
                running = false;
                cancellation.Dispose();
                cancellation = null;
                SetBusy(false);
                cancel.Enabled = false;
                showErrors.Visible = errors.Count > 0;
                if (!wasCanceled && completed == total) SetProgress(1);
                foreach (ListViewItem item in items)
                    if (item.SubItems[2].Text.Contains("중")) item.SubItems[2].Text = wasCanceled ? "취소" : "실패";
                status.Text = string.Format("{0} · 완료 {1}개 / 실패 {2}개", wasCanceled ? "취소됨" : "처리 완료", succeeded, failed);
                if (closeWhenDone) Close();
            }
        }

        private void RequestCancel()
        {
            if (cancellation == null) return;
            cancellation.Cancel();
            cancel.Enabled = false;
            status.Text = "취소 중… 완료된 파일은 유지됩니다.";
        }

        private void OpenOutputFolder()
        {
            try
            {
                string path = Path.GetFullPath(output.Text.Trim());
                if (!Directory.Exists(path)) { MessageBox.Show(this, "변환을 시작하거나 저장 폴더를 먼저 선택하세요.", "저장 폴더"); return; }
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = false });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "폴더 열기 오류"); }
        }

        private void ShowErrors()
        {
            using (var dialog = new Form { Text = "변환 오류 상세", Size = new Size(760, 480), StartPosition = FormStartPosition.CenterParent, BackColor = Background })
            {
                dialog.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                    Dock = DockStyle.Fill, Text = string.Join("\r\n\r\n--------------------\r\n\r\n", errors.ToArray()),
                    BackColor = Surface, ForeColor = ForeColor, Font = new Font("Malgun Gothic", 10) });
                dialog.ShowDialog(this);
            }
        }
    }

    internal sealed class Meter : Control
    {
        private double current;
        public double Value { get { return current; } set { current = value; Invalidate(); } }
        public Meter() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(43, 54, 68));
            using (var brush = new SolidBrush(MainForm.Accent)) e.Graphics.FillRectangle(brush, 0, 0, (float)(Width * current), Height);
        }
    }

    internal sealed class WaveMark : Control
    {
        public WaveMark() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int size = Math.Min(Width, Height);
            using (var brush = new SolidBrush(MainForm.Surface)) e.Graphics.FillEllipse(brush, 0, (Height - size) / 2, size - 1, size - 1);
            int[] bars = new[] { 10, 22, 32, 18, 8 };
            using (var pen = new Pen(MainForm.Accent, Math.Max(2, size / 15F)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                for (int i = 0; i < bars.Length; i++)
                {
                    float x = size * (0.23F + i * 0.135F);
                    float half = size * bars[i] / 96F;
                    e.Graphics.DrawLine(pen, x, Height / 2F - half, x, Height / 2F + half);
                }
            }
        }
    }
}
