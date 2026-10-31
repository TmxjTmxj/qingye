using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ICSharpCode.AvalonEdit.Document;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed class ExportController
    {
        readonly MainWindow owner;

        internal ExportController(MainWindow owner) { this.owner = owner; }

        void Sync(Document doc)
        {
            if (owner.richEdit.IsEditing(doc)) { owner.richEdit.Commit(); return; }
            TextDocument buffer;
            if (owner.buffers.TryGetValue(doc, out buffer) && buffer.Text != doc.Text)
            {
                bool wasDirty = doc.Modified;
                doc.Text = buffer.Text;
                doc.Modified = wasDirty || doc.Modified;
            }
        }

        internal void Export(Document doc)
        {
            if (doc == null) return;
            var formats = FormatExport.Formats(doc);
            if (formats.Count == 0) { owner.SetNotice("此类型没有可导出的格式"); return; }
            string filter = FormatExport.Filter(formats);
            string suggested = Path.GetFileNameWithoutExtension(doc.Title ?? "导出");
            string target = Dialogs.PickExport(owner, suggested, filter);
            if (target == null) return;
            var kind = FormatExport.FormatForExtension(target);
            if (!formats.Contains(kind)) { owner.SetNotice("此文件不能导出为 " + FormatExport.Name(kind)); return; }
            if (doc.Path != null && String.Equals(Path.GetFullPath(target), Path.GetFullPath(doc.Path), StringComparison.OrdinalIgnoreCase))
            {
                owner.SetNotice("导出不能覆盖当前文件，请选择其他文件名");
                return;
            }
            if (kind == ExportFormat.Pdf) { owner.Observe(ExportPdfAsync(doc, target)); return; }
            ExportTo(doc, target, kind);
        }

        internal bool ExportTo(Document doc, string path, ExportFormat format)
        {
            try
            {
                Sync(doc);
                File.WriteAllBytes(path, FormatExport.Bytes(doc, format));
                owner.SetNotice("已导出 " + Path.GetFileName(path));
                return true;
            }
            catch (Exception error) { owner.SetNotice("导出失败：" + error.Message); return false; }
        }

        async Task ExportPdfAsync(Document doc, string target)
        {
            try
            {
                Sync(doc);
                string html = FormatExport.Text(doc, ExportFormat.Html);
                if (!await owner.preview.SaveHtmlAsPdfAsync(html, target)) throw new IOException("PDF 写入失败。");
                owner.SetNotice("已导出 " + Path.GetFileName(target));
            }
            catch (Exception error) { owner.SetNotice("导出失败：" + error.Message); }
            finally { if (owner.showPreview) owner.Observe(owner.UpdatePreviewAsync()); }
        }
    }
}
