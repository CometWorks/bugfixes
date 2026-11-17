using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Sandbox;
using Sandbox.Graphics.GUI;
using Shared.Config;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Settings;

internal sealed class SettingsScreen : MyGuiScreenBase
{
    private readonly PluginConfig config;
    private static readonly PropertyInfo[] Settings = typeof(PluginConfig)
        .GetProperties()
        .Where(property =>
            property.PropertyType == typeof(bool)
            && property.GetCustomAttribute<CategoryAttribute>()?.Category != "Server"
        )
        .OrderBy(property => property.MetadataToken)
        .ToArray();

    public SettingsScreen(PluginConfig config)
        : base(
            new Vector2(0.5f),
            MyGuiConstants.SCREEN_BACKGROUND_COLOR,
            new Vector2(0.52f, 0.72f),
            backgroundTransition: MySandboxGame.Config.UIBkOpacity,
            guiTransition: MySandboxGame.Config.UIOpacity
        )
    {
        this.config = config;
        EnabledBackgroundFade = true;
        m_closeOnEsc = true;
        m_drawEvenWithoutFocus = true;
        CanHideOthers = true;
        CanBeHidden = true;
        CloseButtonEnabled = true;
    }

    public override string GetFriendlyName() => "BugfixesSettings";

    public override void LoadContent()
    {
        base.LoadContent();
        RecreateControls(true);
    }

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Bugfixes Settings");

        AddSetting(Controls, Settings[0], -0.25f);

        const float rowHeight = 0.043f;
        var rows = new MyGuiControlParent
        {
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP,
            Size = new Vector2(0.46f, rowHeight * (Settings.Length - 1)),
        };
        for (var i = 1; i < Settings.Length; i++)
        {
            AddSetting(rows.Controls, Settings[i], -rows.Size.Y / 2 + rowHeight * (i - 0.5f));
        }

        var scroll = new MyGuiControlScrollablePanel(rows)
        {
            Position = new Vector2(0, 0.04f),
            Size = new Vector2(0.48f, 0.49f),
            ScrollbarVEnabled = true,
            CanFocusChildren = true,
        };
        scroll.RefreshInternals();
        Controls.Add(scroll);

        Controls.Add(
            new MyGuiControlLabel(
                position: new Vector2(-0.218f, 0.316f),
                text: "Changes save automatically. Restart the game to apply them.",
                textScale: 0.7f
            )
        );
    }

    private void AddSetting(MyGuiControls controls, PropertyInfo property, float y)
    {
        var label = property.GetCustomAttribute<DisplayNameAttribute>().DisplayName;
        var tooltip = property.GetCustomAttribute<DescriptionAttribute>().Description;
        var checkbox = new MyGuiControlCheckbox(
            position: new Vector2(-0.20f, y),
            toolTip: tooltip,
            isChecked: (bool)property.GetValue(config)
        )
        {
            Name = property.Name,
            IsCheckedChanged = control => property.SetValue(config, control.IsChecked),
        };
        var caption = new MyGuiControlLabel(position: new Vector2(-0.17f, y), text: label);
        caption.SetToolTip(tooltip);
        controls.Add(checkbox);
        controls.Add(caption);
    }
}
