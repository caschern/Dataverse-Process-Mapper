using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using McTools.Xrm.Connection;
using XrmToolBox.Extensibility;

namespace DataverseProcessMapper.UI
{
    /// <summary>
    /// What the tool asks of the desktop: the browser, the clipboard, files and
    /// dialogs. One replaceable object, so tests can check the links, copies and
    /// exports the tool makes without a browser, the real clipboard or a modal
    /// dialog; the tool itself always uses <see cref="Default"/>.
    /// </summary>
    public class DesktopServices
    {
        public static DesktopServices Default { get; } = new DesktopServices();

        /// <summary>
        /// Opens a link in the browser profile the connection is set up with in
        /// XrmToolBox, or the default browser when that fails (or there is no connection).
        /// </summary>
        public virtual void OpenUrl(ConnectionDetail connection, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (connection != null)
            {
                try
                {
                    connection.OpenUrlWithBrowserProfile(new Uri(url));
                    return;
                }
                catch (Exception)
                {
                    // An unset or missing browser profile: the default browser still works.
                }
            }
            Process.Start(url);
        }

        /// <summary>Puts text on the clipboard; throws <see cref="ExternalException"/> when another program holds it.</summary>
        public virtual void SetClipboardText(string text)
        {
            // Retries: another program (a clipboard manager) may hold the clipboard briefly.
            Clipboard.SetDataObject(text, true, 5, 100);
        }

        /// <summary>Opens a file with the program Windows associates with it.</summary>
        public virtual void OpenFile(string path) => Process.Start(path);

        /// <summary>Asks where to save a file; null when the user cancels.</summary>
        public virtual string ChooseSaveFile(IWin32Window owner, string filter, string fileName, string initialDirectory)
        {
            using (var dialog = new SaveFileDialog { Filter = filter, FileName = fileName })
            {
                if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
                    dialog.InitialDirectory = initialDirectory;
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
            }
        }

        /// <summary>Asks for a folder; null when the user cancels.</summary>
        public virtual string ChooseFolder(IWin32Window owner, string description, string selectedPath)
        {
            using (var dialog = new FolderBrowserDialog { Description = description })
            {
                if (!string.IsNullOrEmpty(selectedPath) && Directory.Exists(selectedPath))
                    dialog.SelectedPath = selectedPath;
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        public virtual DialogResult ShowMessage(IWin32Window owner, string text, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon)
            => MessageBox.Show(owner, text, caption, buttons, icon);

        /// <summary>XrmToolBox's error dialog (details, copy, and a GitHub issue link).</summary>
        public virtual void ShowError(PluginControlBase owner, Exception error, string heading, string extraInfo)
            => owner.ShowErrorDialog(error, heading, extraInfo);
    }
}
