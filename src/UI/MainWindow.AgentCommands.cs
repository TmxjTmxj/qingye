using System;
namespace Qingye.UI
{
    // Agent-mode commands shared by the keyboard shortcuts. Keeping them in a
    // partial file leaves MainWindow.cs inside its 250-line orchestration guard.
    public partial class MainWindow
    {
        internal bool AgentVisible { get { return agentVisible; } }
        // The agent shortcuts share one entry so Ctrl+Alt+N/H/M also open a
        // closed panel before acting inside it.
        internal void ShowAgent()
        {
            if (!agentVisible) { agentVisible = true; UpdateToolbar(); }
            agentPanel.FocusInput();
        }
        internal void FocusDocument()
        {
            if (richEdit != null && richEdit.Active) { richEdit.Editor.FocusEditor(); return; }
            if (active != null && active.Editor != null) active.Editor.Focus();
        }
        internal void ToggleAgentHistory()
        {
            if (!agentVisible) { agentVisible = true; UpdateToolbar(); }
            agentPanel.ToggleHistory();
        }
        internal void NewAgentTask()
        {
            if (!agentVisible) { agentVisible = true; UpdateToolbar(); }
            agentPanel.NewConversation();
        }
        internal void StopAgent()
        {
            agentPanel.Stop();
        }
        internal void OpenAgentSettings()
        {
            if (!agentVisible) { agentVisible = true; UpdateToolbar(); }
            agentPanel.OpenModelSettings();
        }
    }
}
