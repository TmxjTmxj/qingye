using System;
using System.Windows;
using System.Windows.Controls;

namespace Qingye.UI
{
    internal sealed class AgentDockLayout
    {
        readonly Grid shell;
        readonly FrameworkElement document;
        readonly SidePanel navigation;
        readonly AgentPanel agent;
        readonly GridSplitter splitter;
        internal bool Focused;
        internal AgentDockLayout(Grid shell, FrameworkElement document, SidePanel navigation, AgentPanel agent, GridSplitter splitter)
        {
            this.shell = shell;
            this.document = document;
            this.navigation = navigation;
            this.agent = agent;
            this.splitter = splitter;
        }
        internal void Apply(double width, bool visible, bool showNavigation, double agentWidth)
        {
            bool focus = visible && (Focused || width < 640);
            bool navigationVisible = showNavigation && !focus && (!visible || width >= 1100);
            navigation.SetVisible(navigationVisible);
            document.Visibility = focus ? Visibility.Collapsed : Visibility.Visible;
            shell.ColumnDefinitions[0].Width = new GridLength(navigationVisible ? 220 : 0);
            shell.ColumnDefinitions[1].Width = new GridLength(navigationVisible ? 8 : 0);
            double available = Math.Max(240, width - 24 - (navigationVisible ? 228 : 0) - 6 - 260);
            shell.ColumnDefinitions[3].Width = new GridLength(visible && !focus ? 6 : 0);
            shell.ColumnDefinitions[4].Width = new GridLength(visible && !focus ? Math.Min(available, Math.Max(240, Math.Min(620, agentWidth))) : 0);
            Grid.SetColumn(agent, focus ? 0 : 4);
            Grid.SetColumnSpan(agent, focus ? 5 : 1);
            splitter.Visibility = visible && !focus ? Visibility.Visible : Visibility.Collapsed;
            agent.SetVisible(visible);
            agent.SetDockLabel(focus);
        }
    }
}
