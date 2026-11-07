using System;
using System.Collections.Generic;
using System.Windows.Documents;
using ICSharpCode.AvalonEdit.Document;
using Qingye.Core;

namespace Qingye.UI
{
    // Owns the Markdown rich-editing session so MainWindow stays an orchestrator.
    internal sealed class RichEditSession
    {
        readonly MainWindow owner;
        MarkdownRichEditor editor;
        Document document;
        bool active;

        internal RichEditSession(MainWindow owner) { this.owner = owner; }
        internal bool IsCreated { get { return editor != null; } }
        internal MarkdownRichEditor Editor
        {
            get
            {
                if (editor == null)
                {
                    editor = new MarkdownRichEditor(owner);
                    editor.ApplyTheme(owner.Appearance);
                    editor.Changed += delegate
                    {
                        if (!active || document == null) return;
                        document.Modified = true;
                        owner.OnBufferChanged();
                    };
                }
                return editor;
            }
        }
        internal void ApplyTheme(Settings settings) { if (editor != null) editor.ApplyTheme(settings); }
        internal bool Active { get { return active; } }
        internal Document Document { get { return document; } }
        internal bool IsEditing(Document doc) { return active && doc == document; }

        internal bool Enter(Document doc)
        {
            if (active && document == doc) return true;
            if (active) Leave();
            TextDocument buffer;
            if (!owner.buffers.TryGetValue(doc, out buffer)) { owner.primary.Bind(doc); owner.buffers.TryGetValue(doc, out buffer); }
            if (buffer != null && buffer.Text != doc.Text) { bool wasDirty = doc.Modified; doc.Text = buffer.Text; doc.Modified = wasDirty || doc.Modified; }
            string reason;
            if (!Editor.Load(doc, out reason)) { owner.SetNotice(reason); return false; }
            document = doc;
            active = true;
            return true;
        }

        internal void Leave()
        {
            if (!active) return;
            Commit();
            if (document != null) owner.viewModes.Remove(document);
            active = false;
            document = null;
            editor.Unload();
        }

        internal void Commit()
        {
            if (!active || document == null) return;
            string markdown = editor.Serialize();
            TextDocument buffer;
            if (owner.buffers.TryGetValue(document, out buffer) && buffer.Text != markdown)
                buffer.Replace(0, buffer.TextLength, markdown);
            if (buffer != null) document.Modified = !buffer.UndoStack.IsOriginalFile;
            editor.MarkCommitted();
        }

        internal void Detach(Document doc)
        {
            if (!active || document != doc) return;
            active = false;
            document = null;
            editor.Unload();
        }
    }
}
