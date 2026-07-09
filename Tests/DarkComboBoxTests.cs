using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using RadioRelay.Client.UI;

namespace RadioRelay.Tests;

public class DarkComboBoxTests
{
    [Fact]
    public void Dark_combo_is_custom_drawn_control_not_native_combobox()
    {
        using var combo = new DarkComboBox();

        Assert.IsAssignableFrom<Control>(combo);
        Assert.False(typeof(ComboBox).IsAssignableFrom(typeof(DarkComboBox)));
        Assert.Equal(Theme.FieldBackground, combo.BackColor);
        Assert.Equal(Theme.Text, combo.ForeColor);
        Assert.Equal(Theme.MonoFont, combo.Font);
    }

    [Fact]
    public void Dark_combo_preserves_selected_item_behavior()
    {
        using var combo = new DarkComboBox();
        var changed = 0;
        combo.SelectedIndexChanged += (_, _) => changed++;

        combo.Items.AddRange(new object[] { "Left", "Both", "Right" });
        combo.SelectedItem = "Both";

        Assert.Equal("Both", combo.SelectedItem);
        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Dark_combo_dropdown_selection_does_not_access_disposed_dropdown()
    {
        using var combo = new DarkComboBox
        {
            Size = new Size(82, 21)
        };
        combo.Items.AddRange(new object[] { "Left", "Both", "Right" });
        combo.SelectedItem = "Left";

        InvokePrivate(combo, "ShowDropDown");
        var dropDown = GetPrivateField<ToolStripDropDown>(combo, "_dropDown");
        var host = Assert.IsType<ToolStripControlHost>(Assert.Single(dropDown.Items.Cast<ToolStripItem>()));
        var listBox = Assert.IsType<ListBox>(host.Control);

        listBox.SelectedIndex = 2;
        InvokeProtected(listBox, "OnClick", EventArgs.Empty);

        Assert.Equal("Right", combo.SelectedItem);
    }

    [Fact]
    public void Dark_combo_collapsed_face_has_no_visible_border_pixels()
    {
        using var combo = new DarkComboBox
        {
            Size = new Size(82, 21)
        };
        combo.Items.AddRange(new object[] { "Left", "Both", "Right" });
        combo.SelectedIndex = 0;

        using var bitmap = new Bitmap(combo.Width, combo.Height);
        combo.DrawToBitmap(bitmap, new Rectangle(Point.Empty, combo.Size));

        Assert.Equal(Theme.FieldBackground.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Theme.FieldBackground.ToArgb(), bitmap.GetPixel(combo.Width - 1, 0).ToArgb());
        Assert.Equal(Theme.FieldBackground.ToArgb(), bitmap.GetPixel(0, combo.Height - 1).ToArgb());
        Assert.Equal(Theme.FieldBackground.ToArgb(), bitmap.GetPixel(combo.Width - 1, combo.Height - 1).ToArgb());
    }

    private static void InvokePrivate(object instance, string methodName)
    {
        instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);
    }

    private static void InvokeProtected(object instance, string methodName, EventArgs args)
    {
        instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, new object[] { args });
    }

    private static T GetPrivateField<T>(object instance, string fieldName) where T : class
    {
        return Assert.IsType<T>(instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance));
    }
}
