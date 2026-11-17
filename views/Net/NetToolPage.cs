using System;
using System.Windows;
using System.Windows.Controls;

namespace SMDWin.Views.Net
{
    /// <summary>
    /// The base of the network tools: a page (not a window) shown in its tab on the Network page. The page stays alive while
    /// the app runs: a running test continues when the tab changes. <see cref="Closed"/> exists only for compatibility with
    /// code written for windows and no longer fires.
    /// </summary>
    public abstract class NetToolPage : System.Windows.Controls.UserControl
    {
        public string Title { get; set; } = "";

        protected NetToolPage()
        {
            Loaded += (_, _) => NetButtonLook.Sweep(this);
        }

        // The page sits directly in the Network page, which already has its margins: the content adds no space of its own.
        protected override void OnContentChanged(object oldContent, object newContent)
        {
            base.OnContentChanged(oldContent, newContent);
            if (newContent is System.Windows.FrameworkElement fe) fe.Margin = new System.Windows.Thickness(0);
        }

#pragma warning disable CS0067
        public event EventHandler? Closed;
#pragma warning restore CS0067

        /// <summary>The long self-test: starts the tool as its Start button would (false = this tool has nothing to start).
        /// Nothing that writes to the disk or changes a setting.</summary>
        public virtual bool SelfTestStart() => false;
        /// <summary>The long self-test: stops what <see cref="SelfTestStart"/> started (also called when nothing runs).</summary>
        public virtual void SelfTestStop() { }

        /// <summary>Opens another tool on the Network page (for example Ping, from Diagnose).</summary>
        public static void OpenTool(string key, string? target = null)
        {
            if (System.Windows.Application.Current?.MainWindow is MainWindow main) main.ShowNetTool(key, target);
        }
    }
}
