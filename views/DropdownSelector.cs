using System.Windows;
// The project also uses WinForms (ImplicitUsings + UseWindowsForms): ComboBox exists in both namespaces -> explicit
// alias (the same convention as in MainWindow.*.cs).
using ComboBox = System.Windows.Controls.ComboBox;

namespace SMDWin.Views
{
    public class DropdownSelector : ComboBox
    {
        static DropdownSelector()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(DropdownSelector),
                new FrameworkPropertyMetadata(typeof(DropdownSelector)));
        }

        public DropdownSelector()
        {
            IsEditable = false; // no text box: there is no caret, in any template
        }
    }
}
