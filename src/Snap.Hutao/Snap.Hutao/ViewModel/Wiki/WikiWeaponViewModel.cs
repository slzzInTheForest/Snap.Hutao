﻿// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

using Microsoft.UI.Xaml.Controls;
using Snap.Hutao.Control.Collection.AdvancedCollectionView;
using Snap.Hutao.Factory.ContentDialog;
using Snap.Hutao.Model.Calculable;
using Snap.Hutao.Model.Entity.Primitive;
using Snap.Hutao.Model.Intrinsic;
using Snap.Hutao.Model.Intrinsic.Frozen;
using Snap.Hutao.Model.Metadata;
using Snap.Hutao.Model.Metadata.Item;
using Snap.Hutao.Model.Metadata.Weapon;
using Snap.Hutao.Model.Primitive;
using Snap.Hutao.Service.Cultivation;
using Snap.Hutao.Service.Hutao;
using Snap.Hutao.Service.Metadata;
using Snap.Hutao.Service.Notification;
using Snap.Hutao.Service.User;
using Snap.Hutao.View.Dialog;
using Snap.Hutao.Web.Response;
using System.Collections.Frozen;
using System.Runtime.InteropServices;
using CalculateAvatarPromotionDelta = Snap.Hutao.Web.Hoyolab.Takumi.Event.Calculate.AvatarPromotionDelta;
using CalculateClient = Snap.Hutao.Web.Hoyolab.Takumi.Event.Calculate.CalculateClient;
using CalculateConsumption = Snap.Hutao.Web.Hoyolab.Takumi.Event.Calculate.Consumption;

namespace Snap.Hutao.ViewModel.Wiki;

/// <summary>
/// 武器资料视图模型
/// </summary>
[ConstructorGenerated]
[Injection(InjectAs.Scoped)]
internal sealed partial class WikiWeaponViewModel : Abstraction.ViewModel, IWikiViewModelInitialization
{
    private readonly IContentDialogFactory contentDialogFactory;
    private readonly CalculateClient calculateClient;
    private readonly ICultivationService cultivationService;
    private readonly ITaskContext taskContext;
    private readonly IMetadataService metadataService;
    private readonly IHutaoSpiralAbyssStatisticsCache hutaoCache;
    private readonly IInfoBarService infoBarService;
    private readonly IUserService userService;

    private AdvancedCollectionView<Weapon>? weapons;
    private Weapon? selected;
    private string? filterText;
    private BaseValueInfo? baseValueInfo;
    private Dictionary<Level, Dictionary<GrowCurveType, float>>? levelWeaponCurveMap;
    private List<Promote>? promotes;
    private FrozenSet<string> availableQueries;

    /// <summary>
    /// 角色列表
    /// </summary>
    public AdvancedCollectionView<Weapon>? Weapons { get => weapons; set => SetProperty(ref weapons, value); }

    /// <summary>
    /// 选中的角色
    /// </summary>
    public Weapon? Selected
    {
        get => selected; set
        {
            if (SetProperty(ref selected, value))
            {
                UpdateBaseValueInfo(value);
            }
        }
    }

    /// <summary>
    /// 基础数值信息
    /// </summary>
    public BaseValueInfo? BaseValueInfo { get => baseValueInfo; set => SetProperty(ref baseValueInfo, value); }

    /// <summary>
    /// 筛选文本
    /// </summary>
    public string? FilterText { get => filterText; set => SetProperty(ref filterText, value); }

    public void Initialize(IAutoSuggestBoxAccessor accessor)
    {
        accessor.AutoSuggestBox.TextChanged += OnFilterSuggestionRequested;
        accessor.AutoSuggestBox.SuggestionChosen += OnFilterSuggestionChosen;
        accessor.AutoSuggestBox.QuerySubmitted += ApplyFilter;
    }

    /// <inheritdoc/>
    protected override async Task OpenUIAsync()
    {
        if (await metadataService.InitializeAsync().ConfigureAwait(false))
        {
            levelWeaponCurveMap = await metadataService.GetLevelToWeaponCurveMapAsync().ConfigureAwait(false);
            promotes = await metadataService.GetWeaponPromoteListAsync().ConfigureAwait(false);
            Dictionary<MaterialId, Material> idMaterialMap = await metadataService.GetIdToMaterialMapAsync().ConfigureAwait(false);

            List<Weapon> weapons = await metadataService.GetWeaponListAsync().ConfigureAwait(false);
            IEnumerable<Weapon> sorted = weapons
                .OrderByDescending(weapon => weapon.RankLevel)
                .ThenBy(weapon => weapon.WeaponType)
                .ThenByDescending(weapon => weapon.Id.Value);
            List<Weapon> list = [.. sorted];

            await CombineComplexDataAsync(list, idMaterialMap).ConfigureAwait(false);

            await taskContext.SwitchToMainThreadAsync();

            Weapons = new(list, true);
            Selected = Weapons.View.ElementAtOrDefault(0);

            availableQueries = FrozenSet.ToFrozenSet(
                [
                    .. weapons.Select(w => w.Name),
                    .. IntrinsicFrozen.ItemQualities,
                    .. IntrinsicFrozen.FightProperties,
                    .. IntrinsicFrozen.WeaponTypes,
                ]);
        }
    }

    private async ValueTask CombineComplexDataAsync(List<Weapon> weapons, Dictionary<MaterialId, Material> idMaterialMap)
    {
        if (await hutaoCache.InitializeForWikiWeaponViewAsync().ConfigureAwait(false))
        {
            ArgumentNullException.ThrowIfNull(hutaoCache.WeaponCollocations);

            foreach (Weapon weapon in weapons)
            {
                weapon.Collocation = hutaoCache.WeaponCollocations.GetValueOrDefault(weapon.Id);
                weapon.CultivationItemsView ??= weapon.CultivationItems.SelectList(i => idMaterialMap.GetValueOrDefault(i, Material.Default));
            }
        }
    }

    [Command("CultivateCommand")]
    private async Task CultivateAsync(Weapon? weapon)
    {
        if (weapon is null)
        {
            return;
        }

        if (userService.Current is null)
        {
            infoBarService.Warning(SH.MustSelectUserAndUid);
            return;
        }

        CalculableOptions options = new(null, weapon.ToCalculable());
        CultivatePromotionDeltaDialog dialog = await contentDialogFactory.CreateInstanceAsync<CultivatePromotionDeltaDialog>(options).ConfigureAwait(false);
        (bool isOk, CalculateAvatarPromotionDelta delta) = await dialog.GetPromotionDeltaAsync().ConfigureAwait(false);

        if (!isOk)
        {
            return;
        }

        Response<CalculateConsumption> consumptionResponse = await calculateClient
            .ComputeAsync(userService.Current.Entity, delta)
            .ConfigureAwait(false);

        if (!consumptionResponse.IsOk())
        {
            return;
        }

        CalculateConsumption consumption = consumptionResponse.Data;
        LevelInformation levelInformation = LevelInformation.From(delta);
        try
        {
            bool saved = await cultivationService
                .SaveConsumptionAsync(CultivateType.Weapon, weapon.Id, consumption.WeaponConsume.EmptyIfNull(), levelInformation)
                .ConfigureAwait(false);

            if (saved)
            {
                infoBarService.Success(SH.ViewModelCultivationEntryAddSuccess);
            }
            else
            {
                infoBarService.Warning(SH.ViewModelCultivationEntryAddWarning);
            }
        }
        catch (Core.ExceptionService.UserdataCorruptedException ex)
        {
            infoBarService.Error(ex, SH.ViewModelCultivationAddWarning);
        }
    }

    private void UpdateBaseValueInfo(Weapon? weapon)
    {
        if (weapon is null)
        {
            BaseValueInfo = null;
            return;
        }

        ArgumentNullException.ThrowIfNull(promotes);
        Dictionary<PromoteLevel, Promote> weaponPromoteMap = promotes.Where(p => p.Id == weapon.PromoteId).ToDictionary(p => p.Level);
        List<PropertyCurveValue> propertyCurveValues = weapon.GrowCurves
            .SelectList(curveInfo => new PropertyCurveValue(curveInfo.Type, curveInfo.Value, curveInfo.InitValue));

        ArgumentNullException.ThrowIfNull(levelWeaponCurveMap);
        BaseValueInfo = new(weapon.MaxLevel, propertyCurveValues, levelWeaponCurveMap, weaponPromoteMap);
    }

    private void OnFilterSuggestionRequested(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (Weapons is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return;
        }

        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = availableQueries.Where(q => q.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void OnFilterSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        sender.Text = args.SelectedItem.ToString();
    }

    private void ApplyFilter(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (Weapons is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(FilterText))
        {
            Weapons.Filter = default!;
            return;
        }

        Weapons.Filter = WeaponFilter.Compile(FilterText);

        if (Selected is not null && Weapons.Contains(Selected))
        {
            return;
        }

        try
        {
            Weapons.MoveCurrentToFirst();
        }
        catch (COMException)
        {
        }
    }
}