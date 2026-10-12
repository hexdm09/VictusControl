using VictusControl.Core;

namespace VictusControl.App;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _ecoItem;
    private readonly ToolStripMenuItem _balancedItem;
    private readonly ToolStripMenuItem _perfItem;

    public TrayContext()
    {
        _ecoItem = new ToolStripMenuItem("ECO", null, (_, _) => Apply(PowerProfile.Eco));
        _balancedItem = new ToolStripMenuItem("Balanced", null, (_, _) => Apply(PowerProfile.Balanced));
        _perfItem = new ToolStripMenuItem("Performance", null, (_, _) => Apply(PowerProfile.Performance));

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([_ecoItem, _balancedItem, _perfItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread())]);

        _icon = new NotifyIcon
        {
            Text = "VictusControl",
            Icon = SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => Cycle();
    }

    private static void Apply(PowerProfile profile)
    {
        try
        {
            using var client = new HpWmiClient();
            new PowerController(client).ApplyProfile(profile);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "VictusControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Cycle()
    {
        try
        {
            using var client = new HpWmiClient();
            var controller = new PowerController(client);
            GpuPowerData gpu = controller.GetGpuPower();
            PowerProfile next = (gpu.CustomTgp, gpu.Ppab) switch
            {
                (0x01, 0x01) => PowerProfile.Eco,
                (_, 0x01) => PowerProfile.Performance,
                _ => PowerProfile.Balanced,
            };
            controller.ApplyProfile(next);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "VictusControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _icon.Dispose();
        base.Dispose(disposing);
    }
}
