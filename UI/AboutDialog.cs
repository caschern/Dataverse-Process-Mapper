using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace DataverseProcessMapper.UI
{
    /// <summary>The tool's About box: name, version, where the project lives, and the third-party notices.</summary>
    public sealed class AboutDialog : Form
    {
        public const string ToolName = "Dataverse Process Mapper";
        public const string RepositoryUrl = "https://github.com/caschern/Dataverse-Process-Mapper";

        // Embedded under this fixed name by DataverseProcessMapper.csproj.
        private const string NoticesResource = "DataverseProcessMapper.THIRD-PARTY-NOTICES.md";

        private readonly DesktopServices _services;
        private readonly Font _titleFont;

        protected override void Dispose(bool disposing)
        {
            if (disposing) _titleFont.Dispose();
            base.Dispose(disposing);
        }

        public AboutDialog(DesktopServices services, Image logo)
        {
            _services = services ?? DesktopServices.Default;

            Text = "About " + ToolName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            // Laid out for the default 8.25 pt font at 96 dpi; scaled from there.
            AutoScaleDimensions = new SizeF(6F, 13F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(560, 440);
            Padding = new Padding(12);

            var picture = new PictureBox
            {
                Image = logo,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(48, 48),
                Location = new Point(12, 12)
            };
            _titleFont = new Font(Font.FontFamily, Font.Size * 1.4f, FontStyle.Bold);
            var name = new Label
            {
                Text = ToolName,
                Font = _titleFont,
                AutoSize = true,
                Location = new Point(72, 12)
            };
            var version = new Label
            {
                Name = "version",
                Text = "Version " + Version,
                AutoSize = true,
                Location = new Point(74, 42)
            };
            var link = new LinkLabel
            {
                Name = "repository",
                Text = RepositoryUrl,
                AutoSize = true,
                Location = new Point(74, 62)
            };
            link.LinkClicked += (s, e) => _services.OpenUrl(null, RepositoryUrl);

            var noticesLabel = new Label
            {
                Text = "Third-party notices",
                AutoSize = true,
                Location = new Point(12, 92)
            };
            var notices = new TextBox
            {
                Name = "notices",
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Text = PlainText(Notices),
                Location = new Point(12, 112),
                Size = new Size(536, 280),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                BackColor = SystemColors.Window
            };
            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Size = new Size(80, 26),
                Location = new Point(468, 402),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            AcceptButton = ok;
            CancelButton = ok;

            Controls.AddRange(new Control[] { picture, name, version, link, noticesLabel, notices, ok });
            ActiveControl = ok; // not the notices: a selected wall of text is the first thing seen otherwise
        }

        /// <summary>The tool's version, from its assembly.</summary>
        public static string Version
        {
            get
            {
                var assembly = typeof(AboutDialog).Assembly;
                var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                // The SDK can append "+commit" to the informational version.
                if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+')[0];
                return assembly.GetName().Version.ToString(3);
            }
        }

        /// <summary>
        /// Markdown as plain text for a text box: no heading marks, code fences or
        /// backticks; headings underlined instead.
        /// </summary>
        public static string PlainText(string markdown)
        {
            var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
            var text = new StringBuilder();
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (line.StartsWith("```", StringComparison.Ordinal)) continue;
                var heading = line.TrimStart('#');
                if (heading.Length < line.Length && heading.StartsWith(" ", StringComparison.Ordinal))
                {
                    heading = heading.Trim();
                    text.Append(heading).Append("\r\n")
                        .Append(new string(line.StartsWith("##", StringComparison.Ordinal) ? '-' : '=', heading.Length)).Append("\r\n");
                    continue;
                }
                text.Append(line.Replace("`", "")).Append("\r\n");
            }
            return text.ToString().TrimEnd() + "\r\n";
        }

        /// <summary>THIRD-PARTY-NOTICES.md as shipped with the tool.</summary>
        public static string Notices
        {
            get
            {
                using (var stream = typeof(AboutDialog).Assembly.GetManifestResourceStream(NoticesResource))
                {
                    if (stream == null) return "See THIRD-PARTY-NOTICES.md next to the tool, or " + RepositoryUrl;
                    using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
                }
            }
        }
    }
}
