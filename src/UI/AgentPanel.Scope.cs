using System;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed partial class AgentPanel
    {
        string editorScopeSignature;
        bool synchronizingScope;

        static string ScopeSignature(string root, string file)
        {
            return root != null ? "directory:" + root : file != null ? "file:" + file : "";
        }

        void SynchronizeEditorScope()
        {
            // Retired native-controller tests keep their explicit persisted
            // scope contract; production SDK sessions follow the editor.
            if (legacyTesting || synchronizingScope || shuttingDown) return;
            string root = owner.WorkspaceRoot;
            string file = owner.Current == null ? null : owner.Current.Path;
            string next = ScopeSignature(root, file);
            bool changed = editorScopeSignature != null &&
                !String.Equals(editorScopeSignature, next, StringComparison.OrdinalIgnoreCase);
            editorScopeSignature = next;
            synchronizingScope = true;
            try
            {
                if (changed)
                {
                    RetireSdkController();
                    if (cancellation != null) cancellation.Cancel();
                    QueueSessionMaintenance(SdkAgentController.ClearTemporarySessionsAsync);
                    store.Clear();
                    controllerScope = controllerConfig = "";
                    resumedHarnessId = null;
                    permission.SelectedIndex = (int)HarnessFilePermission.Confirm;
                    Adopt(new AgentSession());
                    ResetExtras();
                    lastPrompt = null;
                    elapsed.Text = "";
                    RenderSession();
                }
                session.Root = root;
                session.CurrentFile = file;
            }
            finally { synchronizingScope = false; }
        }
    }
}
