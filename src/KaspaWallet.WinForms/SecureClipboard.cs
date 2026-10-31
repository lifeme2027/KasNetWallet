namespace KaspaWallet.WinForms;

/// <summary>Clipboard writes excluded from Win+V history/cloud sync, auto-cleared after N seconds if unchanged.</summary>
internal static class SecureClipboard
{
    public static void Set(string text, int clearAfterSeconds)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0 }));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        Clipboard.SetDataObject(data, true);
        if (clearAfterSeconds <= 0) return;
        var t = new System.Windows.Forms.Timer { Interval = clearAfterSeconds * 1000 };
        t.Tick += (_, _) =>
        {
            t.Stop(); t.Dispose();
            try { if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear(); } catch { }
        };
        t.Start();
    }
}
