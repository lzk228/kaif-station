
using Content.Server.Antag;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.Administration.Commands;
using Content.Server.Humanoid;
using Content.Server.Mind;
using Content.Server.Roles;
using Content.Server.Store.Components;
using Content.Server.Store.Systems;
using Content.Shared.Roles;
using Content.Shared.CCVar;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Inventory;
using Content.Shared.Antag;
using Content.Shared.Store;
using Content.Server.Station.Systems;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Random;
using Robust.Shared.Utility;
using System.Linq;
using Content.Server.Administration.Managers;
using Content.Server.Station.Components;
using Content.Server.NPC.Components;
using Content.Shared.Preferences;
using Content.Server.NPC.Systems;
using Content.Server.Spawners.Components;
using Content.Server.Weapons.Melee.WeaponRandom;
using JetBrains.Annotations;
using System.Reflection.Metadata.Ecma335;
using Content.Shared.NPC.Systems;
using Content.Server.Preferences.Managers;

namespace Content.Server.GameTicking.Rules;

public sealed class WizardRuleSystem : GameRuleSystem<WizardRuleComponent>
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IAdminManager _adminManager = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IServerPreferencesManager _pref = default!;
    [Dependency] private readonly HumanoidAppearanceSystem _humanoid = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly MindSystem _mindSystem = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly SharedRoleSystem _roles = default!;
    [Dependency] private readonly StationSpawningSystem _stationSpawning = default!;
    [Dependency] private readonly StoreSystem _store = default!;
    [Dependency] private readonly AntagSelectionSystem _antagSelection = default!;
    [Dependency] private readonly InventorySystem _inventoryManager = default!;
    [Dependency] private readonly EntityManager _entityManager = default!;

    private ISawmill _sawmill = default!;


    [ValidatePrototypeId<AntagPrototype>]
    private const string WizardId = "Wizard";

    [ValidatePrototypeId<CurrencyPrototype>]
    private const string MagipointsCurrencyPrototype = "Magipoint";

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("Wizards");
        // SubscribeLocalEvent<RoundStartAttemptEvent>(OnStartAttempt);
        // SubscribeLocalEvent<RulePlayerSpawningEvent>(OnPlayersSpawning);
        //  SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEndText);
        SubscribeLocalEvent<RulePlayerJobsAssignedEvent>(OnPlayersSpawned);

    }


    private void OnPlayersSpawned(RulePlayerJobsAssignedEvent ev)
    {
        // I took this one from the Thief Rule System lol
        var query = QueryActiveRules();
        while (query.MoveNext(out var uid, out _, out var comp, out var gameRule))
        {
            // Finds eligible players to add to the list
            var eligiblePlayers = _antagSelection.GetEligiblePlayers(ev.Players, comp.WizardPrototypeId, acceptableAntags: AntagAcceptability.All, allowNonHumanoids: true);

            // No eligible players = no wizards
            if (eligiblePlayers.Count == 0)
            {
                Log.Warning($"No eligible wizards found, ending game rule {ToPrettyString(uid):rule}");
                GameTicker.EndGameRule(uid, gameRule);
                continue;
            }

            //Calculate number of wizards to choose
            var wizardCount = _random.Next(1, comp.MaxWizards + 1);

            //Select our wizards
            var wizards = _antagSelection.ChooseAntags(wizardCount, eligiblePlayers);

            MakeWizard(wizards, comp);
        }
    }

    public void MakeWizard(List<EntityUid> players, WizardRuleComponent wizardRule)
    {
        foreach (var wizard in players)
        {
            MakeWizard(wizard, wizardRule);
        }
    }

    public void MakeWizard(EntityUid player, WizardRuleComponent wizardRule)
    {

        // no mind = no magic (real)
        if (!_mindSystem.TryGetMind(player, out var mindId, out var mind))
            return;

        // players who are already wizards cannot become wizards a second time
        if (HasComp<WizardRoleComponent>(mindId))
            return;

        // no session would break things
        if (!_mindSystem.TryGetSession(mind, out var session))
            return;

        wizardRule.Wizards.Add(session);


        _roles.MindAddRole(mindId, new WizardRoleComponent
        {
            PrototypeId = wizardRule.WizardPrototypeId,
        }, silent: true);

        // SpawnItems(player);

    }

    public void SetupWizard(EntityUid mob, string name, HumanoidCharacterProfile? profile, WizardRuleComponent component)
    {
        _metaData.SetEntityName(mob, name);
        EnsureComp<WizardRoleComponent>(mob);

        if (profile != null)
            _humanoid.LoadProfile(mob, profile);

        var gear = _prototypeManager.Index(component.GearProto);
        _stationSpawning.EquipStartingGear(mob, gear, profile);

        _npcFaction.RemoveFaction(mob, "NanoTrasen", false);
        _npcFaction.AddFaction(mob, "Wizard Federation");

    }

    public void SpawnWizards(WizardRuleComponent component)
    {
        if (component.WizardOutpost is not { Valid: true } outpostUid)
            return;

        var spawns = new List<EntityCoordinates>();
        foreach (var (_, meta, xform) in EntityQuery<SpawnPointComponent, MetaDataComponent, TransformComponent>(true))
        {
            if (meta.EntityPrototype?.ID != component.SpawnPointProto.Id)
                continue;

            if (xform.ParentUid != component.WizardOutpost)
                continue;

            spawns.Add(xform.Coordinates);
            break;
        }
        if (spawns.Count == 0)
        {
            spawns.Add(Transform(outpostUid).Coordinates);
        }

        foreach (var session in component.Wizards)
        {
            if (session != null)
            {
                var profile = _pref.GetPreferences(session.UserId).SelectedCharacter as HumanoidCharacterProfile;
                if (!_prototypeManager.TryIndex(profile?.Species ?? SharedHumanoidAppearanceSystem.DefaultSpecies, out SpeciesPrototype? species))
                {
                    species = _prototypeManager.Index<SpeciesPrototype>(SharedHumanoidAppearanceSystem.DefaultSpecies);
                }

                var mob = Spawn(species.Prototype, RobustRandom.Pick(spawns));
                SetupWizard(mob, "Default", profile, component);

                var newMind = _mindSystem.CreateMind(session.UserId, "Default");
                _mindSystem.SetUserId(newMind, session.UserId);
                _roles.MindAddRole(newMind, new WizardRoleComponent
                {
                    PrototypeId = component.WizardPrototypeId,
                }, silent: true);

                _mindSystem.TransferTo(newMind, mob);
            }
    }
    }



    public void SpawnItems(EntityUid player)
    {
        // holy shit this is so jank
        // TODO - Make this system run through a gear prototype thing
        EntityManager.TryGetComponent(player, out InventoryComponent? inventoryComponent);
        RemoveItems(player);
        _inventoryManager.SpawnItemInSlot(player, "shoes", "ClothingShoesWizard", true, true, inventoryComponent);
        _inventoryManager.SpawnItemInSlot(player, "jumpsuit", "ClothingUniformJumpsuitColorDarkBlue", true, true, inventoryComponent);
        _inventoryManager.SpawnItemInSlot(player, "back", "ClothingBackpackFilled", true, false, inventoryComponent);
        _inventoryManager.SpawnItemInSlot(player, "head", "ClothingHeadHatWizard", true, true, inventoryComponent);
        _inventoryManager.SpawnItemInSlot(player, "outerClothing", "ClothingOuterWizard", true, true, inventoryComponent);
        _inventoryManager.SpawnItemInSlot(player, "id", "WizardPDA", true, true, inventoryComponent);
        _inventoryManager.SpawnItemInSlot(player, "ears", "ClothingHeadsetService", true, true, inventoryComponent);
        _inventoryManager.SpawnItemInSlot(player, "innerClothingSkirt", "ClothingUniformJumpskirtColorDarkBlue", true, false, inventoryComponent);
        _inventoryManager.SpawnItemInSlot(player, "satchel", "ClothingBackpackSatchelFilled", true, false, inventoryComponent);
        _inventoryManager.SpawnItemInSlot(player, "duffelbag", "ClothingBackpackDuffelFilled", true, false, inventoryComponent);
    }

    public void RemoveItems(EntityUid player)
    {
        // checks for pre-existing items and deletes them
        EntityManager.TryGetComponent(player, out InventoryComponent? inventoryComponent);
        if (_inventoryManager.TryGetSlots(player, out var slots))
        {
            foreach (var slot in slots)
            {
                if (_inventoryManager.TryGetSlotEntity(player, slot.Name, out var itemUid, inventoryComponent))
                {
                    _entityManager.DeleteEntity(itemUid);
                }
            }
        }
    }

    public void AdminMakeWizard(EntityUid entity)
    {
        // Admin Verb for wizard-ification
        if (!_mindSystem.TryGetMind(entity, out var mindId, out var mind))
            return;

        SpawnItems(entity);

        var wizardRule = EntityQuery<WizardRuleComponent>().FirstOrDefault();
        if (wizardRule == null)
        {
            // Adds wizard gamerule if not already existing
            GameTicker.StartGameRule("Wizard", out var ruleEntity);
            wizardRule = Comp<WizardRuleComponent>(ruleEntity);
        }



    }

    private bool SpawnMap(Entity<WizardRuleComponent> ent)
    {
        ent.Comp.WizardPlanet = _mapManager.CreateMap();
        var gameMap = _prototypeManager.Index(ent.Comp.OutpostMapPrototype);
        ent.Comp.WizardOutpost = GameTicker.LoadGameMap(gameMap, ent.Comp.WizardPlanet.Value, null)[0];
        var query = EntityQueryEnumerator<WizardShuttleComponent, TransformComponent>();
        while (query.MoveNext(out var grid, out _, out var shuttleTransform))
        {
            if (shuttleTransform.MapID != ent.Comp.WizardPlanet)
                continue;

            ent.Comp.WizardShuttle = grid;
            break;
        }
        return true;
    }


    private void MoveWizards(WizardRuleComponent component)
    {


    }
}
