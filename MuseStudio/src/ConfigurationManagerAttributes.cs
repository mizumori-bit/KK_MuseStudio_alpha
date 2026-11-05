// The class ConfigurationManager (the settings window, F1) reads a setting's display from, by its name - the fields of its
// template (game/BepInEx/plugins/ConfigurationManager/ConfigurationManagerAttributes.cs) that MuseStudio uses.
#pragma warning disable 0169, 0414, 0649
internal sealed class ConfigurationManagerAttributes
{
    public string Category;
    public string DispName;
    public int? Order;
    public System.Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawer;     // (the reset buttons: drawn by MuseStudio)
    public bool? HideDefaultButton;
}
