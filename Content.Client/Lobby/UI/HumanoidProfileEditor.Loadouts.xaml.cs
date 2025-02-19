using System.Linq;
using Content.Client.Administration.UI;
using Content.Client.Lobby.UI.Loadouts;
using Content.Client.UserInterface.Controls;
using Content.Shared.CCVar;
using Content.Shared.Clothing.Components;
using Content.Shared.Clothing.Loadouts.Prototypes;
using Content.Shared.Clothing.Loadouts.Systems;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Map;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    private const string Uncategorized = "Uncategorized";

    private readonly HashSet<LoadoutPreferenceSelector> _loadoutPreferences = new();
    private readonly Dictionary<LoadoutPrototype, bool> _loadouts = new();
    private Dictionary<string, EntityUid> _dummyLoadouts = new();

    // Used for quick searching for specific loadout tabs
    private readonly Dictionary<string, LoadoutTabPanel> _loadoutTabs = new();

    private void InitializeLoadouts()
    {
        // Set up the loadouts tab
        LoadoutsTab.Orphan();
        CTabContainer.AddTab(LoadoutsTab, Loc.GetString("humanoid-profile-editor-loadouts-tab"));

        // Show/Hide the loadouts tab if they ever get enabled/disabled
        var loadoutsEnabled = _cfgManager.GetCVar(CCVars.GameLoadoutsEnabled);
        CTabContainer.SetTabVisible(4, loadoutsEnabled);
        ShowLoadouts.Visible = loadoutsEnabled;
        _cfgManager.OnValueChanged(CCVars.GameLoadoutsEnabled, LoadoutsChanged);

        LoadoutsShowUnusableButton.OnToggled += args => UpdateLoadouts(args.Pressed);
        LoadoutsRemoveUnusableButton.OnPressed += _ => TryRemoveUnusableLoadouts();

        UpdateLoadouts(false);
    }

    private void LoadoutsChanged(bool enabled)
    {
        CTabContainer.SetTabVisible(4, enabled);
        ShowLoadouts.Visible = enabled;
    }

    private void UpdateLoadoutPreferences()
    {
        var points = _cfgManager.GetCVar(CCVars.GameLoadoutsPoints);
        LoadoutPointsBar.Value = points;
        LoadoutPointsLabel.Text = Loc.GetString(
            "humanoid-profile-editor-loadouts-points-label", ("points", points), ("max", points));

        foreach (var preferenceSelector in _loadoutPreferences)
        {
            var loadoutId = preferenceSelector.Loadout.ID;
            var loadoutPreference = Profile?.LoadoutPreferences.FirstOrDefault(l => l.LoadoutName == loadoutId) ??
                preferenceSelector.Preference;
            var preference = new LoadoutPreference(
                    loadoutPreference.LoadoutName,
                    loadoutPreference.CustomName,
                    loadoutPreference.CustomDescription,
                    loadoutPreference.CustomColorTint,
                    loadoutPreference.CustomHeirloom)
                { Selected = loadoutPreference.Selected };

            preferenceSelector.Preference = preference;

            if (preference.Selected)
            {
                points -= preferenceSelector.Loadout.Cost;
                LoadoutPointsBar.Value = points;
                LoadoutPointsLabel.Text = Loc.GetString(
                    "humanoid-profile-editor-loadouts-points-label", ("points", points),
                    ("max", LoadoutPointsBar.MaxValue));
            }
        }

        // Set the remove unusable button's label to have the correct amount of unusable loadouts
        LoadoutsRemoveUnusableButton.Text = Loc.GetString(
            "humanoid-profile-editor-loadouts-remove-unusable-button",
            ("count", _loadouts
                .Where(
                    l => _loadoutPreferences
                        .Where(lps => lps.Preference.Selected).Select(lps => lps.Loadout).Contains(l.Key))
                .Count(
                    l => !l.Value
                        || !_loadoutPreferences.First(lps => lps.Loadout == l.Key).Wearable)));
        AdminUIHelpers.RemoveConfirm(LoadoutsRemoveUnusableButton, _confirmationData);

        IsDirty = true;
        ReloadProfilePreview();
    }

    public void UpdateLoadouts(bool? showUnusable = null, bool reload = false)
    {
        showUnusable ??= LoadoutsShowUnusableButton.Pressed;

        // Reset loadout points so you don't get -14 points or something for no reason
        var points = _cfgManager.GetCVar(CCVars.GameLoadoutsPoints);
        LoadoutPointsLabel.Text = Loc.GetString(
            "humanoid-profile-editor-loadouts-points-label", ("points", points), ("max", points));
        LoadoutPointsBar.MaxValue = points;
        LoadoutPointsBar.Value = points;

        // Reset the whole UI and delete caches
        if (reload)
        {
            foreach (var tab in LoadoutsTabs.Tabs)
                LoadoutsTabs.RemoveTab(tab);
            foreach (var uid in _dummyLoadouts)
                _entManager.QueueDeleteEntity(uid.Value);
            _loadoutPreferences.Clear();
        }

        // Get the highest priority job to use for loadout filtering
        var highJob = _controller.GetPreferredJob(Profile ?? HumanoidCharacterProfile.DefaultWithSpecies());

        _loadouts.Clear();
        foreach (var loadout in _prototypeManager.EnumeratePrototypes<LoadoutPrototype>())
        {
            var usable = _characterRequirementsSystem.CheckRequirementsValid(
                loadout.Requirements,
                highJob ?? new JobPrototype(),
                Profile ?? HumanoidCharacterProfile.DefaultWithSpecies(),
                _requirements.GetRawPlayTimeTrackers(),
                _requirements.IsWhitelisted(),
                loadout,
                _entManager,
                _prototypeManager,
                _cfgManager,
                out _
            );
            _loadouts.Add(loadout, usable);

            var list = _loadoutPreferences.ToList();
            if (list.FindIndex(lps => lps.Loadout.ID == loadout.ID) is not (not -1 and var i))
                continue;

            var selector = list[i];
            UpdateSelector(selector, usable);
        }

        if (_loadouts.Count == 0)
        {
            LoadoutsTabs.AddTab(
                new Label { Text = Loc.GetString("humanoid-profile-editor-loadouts-no-loadouts") },
                Loc.GetString("loadout-category-Uncategorized"));
            return;
        }

        if (!_loadoutTabs.ContainsKey(Uncategorized))
        {
            var uncategorized = new LoadoutTabPanel(Uncategorized);
            _loadoutTabs.Add(Uncategorized, uncategorized);
            LoadoutsTabs.AddTab(uncategorized, Loc.GetString("loadout-category-Uncategorized"));
        }

        // Create a Dictionary/tree of categories and subcategories
        var cats = CreateTree(
            _prototypeManager.EnumeratePrototypes<LoadoutCategoryPrototype>().Where(c => c.Root)
                .OrderBy(c => Loc.GetString($"loadout-category-{c.ID}")).ToList());
        var categories = new Dictionary<string, object>();
        foreach (var (key, value) in cats)
            categories.Add(key, value);

        // Create the UI elements for the category tree
        CreateCategoryUI(categories, LoadoutsTabs);

        // Fill categories with loadouts
        foreach (var (loadout, usable) in _loadouts
            .OrderBy(l => l.Key.ID)
            .ThenBy(l => Loc.GetString($"loadout-name-{l.Key.ID}"))
            .ThenBy(l => l.Key.Cost))
        {
            if (_loadoutPreferences.Select(lps => lps.Loadout.ID).Contains(loadout.ID))
            {
                var first = _loadoutPreferences.First(lps => lps.Loadout.ID == loadout.ID);
                var prof = Profile?.LoadoutPreferences.FirstOrDefault(lp => lp.LoadoutName == loadout.ID);
                first.Preference = new(
                    loadout.ID, prof?.CustomName, prof?.CustomDescription, prof?.CustomColorTint, prof?.CustomHeirloom);
                UpdateSelector(first, usable);
                continue;
            }

            var selector = new LoadoutPreferenceSelector(
                loadout, highJob ?? new JobPrototype(),
                Profile ?? HumanoidCharacterProfile.DefaultWithSpecies(), ref _dummyLoadouts,
                _entManager, _prototypeManager, _cfgManager, _characterRequirementsSystem, _requirements,
                new(loadout.ID));
            UpdateSelector(selector, usable);
            AddSelector(selector);

            // Look for an existing category tab. If there is no category put it in Uncategorized (this shouldn't happen)
            var category = _loadoutTabs.TryGetValue(loadout.Category, out var tab) ? tab : _loadoutTabs[Uncategorized];
            category.ItemsContainer.AddChild(selector);
        }

        // Hide any empty tabs
        // HideEmptyTabs(_prototypeManager.EnumeratePrototypes<LoadoutCategoryPrototype>().ToList());

        UpdateLoadoutPreferences();
        return;

        void UpdateSelector(LoadoutPreferenceSelector selector, bool usable)
        {
            selector.Valid = usable;
            selector.ShowUnusable = showUnusable.Value;

            foreach (var item in selector.Loadout.Items)
            {
                if (_dummyLoadouts.TryGetValue(
                        selector.Loadout.ID + selector.Loadout.Items.IndexOf(item), out var entity)
                    && _entManager.GetComponent<MetaDataComponent>(entity).EntityPrototype!.ID == item)
                {
                    if (!_entManager.HasComponent<ClothingComponent>(entity))
                    {
                        selector.Wearable = true;
                        continue;
                    }

                    selector.Wearable = _characterRequirementsSystem.CanEntityWearItem(PreviewDummy, entity);
                    continue;
                }

                entity = _entManager.SpawnEntity(item, MapCoordinates.Nullspace);
                _dummyLoadouts[selector.Loadout.ID + selector.Loadout.Items.IndexOf(item)] = entity;

                if (!_entManager.HasComponent<ClothingComponent>(entity))
                {
                    selector.Wearable = true;
                    continue;
                }

                selector.Wearable = _characterRequirementsSystem.CanEntityWearItem(PreviewDummy, entity);
            }
        }

        void CreateCategoryUI(Dictionary<string, object> tree, NeoTabContainer parent)
        {
            foreach (var (key, value) in tree)
            {
                // If the category's container exists already, ignore it
                if (parent.Contents.Any(c => c.Name == key))
                    continue;

                // If the value is a list of LoadoutPrototypes, create a final tab for them
                if (value is List<LoadoutPrototype>)
                {
                    var category = new LoadoutTabPanel(key);
                    _loadoutTabs.Add(key, category);
                    parent.AddTab(category, Loc.GetString($"loadout-category-{key}"));
                }
                // If the value is a dictionary, create a new tab for it and recursively call this function to fill it
                else
                {
                    var category = new NeoTabContainer
                    {
                        Name = key,
                        HorizontalExpand = true,
                        VerticalExpand = true,
                        SeparatorMargin = new(0)
                    };

                    parent.AddTab(category, Loc.GetString($"loadout-category-{key}"));
                    CreateCategoryUI((Dictionary<string, object>)value, category);
                }
            }
        }

        void AddSelector(LoadoutPreferenceSelector selector)
        {
            _loadoutPreferences.Add(selector);
            selector.PreferenceChanged += preference =>
            {
                // Make sure they have enough loadout points
                var selected = preference.Selected
                    ? CheckPoints(-selector.Loadout.Cost, preference.Selected)
                    : CheckPoints(selector.Loadout.Cost, preference.Selected);

                // Update Preferences
                Profile = Profile?.WithLoadoutPreference(
                    selector.Loadout.ID,
                    selected,
                    preference.CustomName,
                    preference.CustomDescription,
                    preference.CustomColorTint,
                    preference.CustomHeirloom);
                IsDirty = true;
                UpdateLoadoutPreferences();
                SetProfile(Profile, CharacterSlot);
            };
        }

        bool CheckPoints(int pointsRequired, bool preference)
        {
            var temp = LoadoutPointsBar.Value + pointsRequired;
            return preference ? !(temp < 0) : temp < 0;
        }
    }

    private Dictionary<string, object> CreateTree(List<LoadoutCategoryPrototype> cats)
    {
        var tree = new Dictionary<string, object>();
        foreach (var category in cats)
        {
            // If the category is already in the tree, ignore it
            if (tree.ContainsKey(category.ID))
                continue;

            // Categories don't have a Parent field, so we need to instead check the SubCategories of every Category
            var subCategories = category.SubCategories.Where(subCategory => !tree.ContainsKey(subCategory)).ToList();
            // If there are no subcategories, add a loadout spot to the dictionary
            if (subCategories.Count == 0)
            {
                tree.Add(category.ID, new List<LoadoutPrototype>());
                continue;
            }

            // If there are subcategories, we need to add them to the dictionary as well
            var subCategoryTree = CreateTree(subCategories.Select(c => _prototypeManager.Index(c)).ToList());
            tree.Add(category.ID, subCategoryTree);
        }

        return tree;
    }

    private BoxContainer? FindCategory(string id, NeoTabContainer parent)
    {
        BoxContainer? match = null;
        foreach (var child in parent.Contents)
        {
            if (string.IsNullOrEmpty(child.Name) || child.Name != id)
                continue;

            match = (BoxContainer)child;
        }

        if (match is not null)
            return match;

        return parent.Contents.Where(c => c is NeoTabContainer).Cast<NeoTabContainer>()
            .Select(subcategory => FindCategory(id, subcategory)).FirstOrDefault();
    }

    private void HideEmptyTabs(List<LoadoutCategoryPrototype> cats)
    {
        foreach (var tab in cats.Select(category => _loadoutTabs[category.ID]))
        {
            // If it's empty, hide it
            ((NeoTabContainer)tab.Parent!.Parent!.Parent!.Parent!).SetTabVisible(
                tab, tab.Children.First().Children.First().Children.Any());

            // If it has a parent tab container, hide it if it's empty
            if (tab.Parent?.Parent is NeoTabContainer parent)
            {
                var parentCats = parent.Contents.Select(c => _prototypeManager.Index<LoadoutCategoryPrototype>(c.Name!))
                    .ToList();
                HideEmptyTabs(parentCats);
            }
        }
    }

    private void TryRemoveUnusableLoadouts()
    {
        // Confirm the user wants to remove unusable loadouts
        if (!AdminUIHelpers.TryConfirm(LoadoutsRemoveUnusableButton, _confirmationData))
            return;

        // Remove unusable and unwearable loadouts
        foreach (var (loadout, _) in
            _loadouts.Where(
                l =>
                    !l.Value || !_loadoutPreferences.First(lps => lps.Loadout.ID == l.Key.ID).Wearable).ToList())
            Profile = Profile?.WithLoadoutPreference(loadout.ID, false);
        UpdateCharacterRequired();
    }
}
