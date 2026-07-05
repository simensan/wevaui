using System.Collections.Generic;
using UnityEngine;
using Weva;
using Weva.Binding;
using Weva.Dom;
using Weva.Events;

namespace GameMenu.UI.WevaMenu {
    // Attach to the same GameObject as a Weva WevaDocument; assign main-menu.html
    // + main-menu.css TextAssets in the inspector. Mock data lives here so the
    // screen renders standalone; swap in the live MainMenuController feed later.
    public sealed class MainMenuWevaController : MonoBehaviour, IBindingVersion {
        // IBindingVersion: every event handler that mutates bound state (or a
        // VM reachable from one) bumps this so idle frames skip the binding
        // poll. If you write the public Hero*/PlayerName setters from outside,
        // call BumpBindings() afterwards.
        public int BindingVersion { get; private set; }
        public void BumpBindings() => BindingVersion++;

        [UIBind] public string HeroName { get; set; } = "Aptus";
        [UIBind] public string HeroLevel { get; set; } = "18";
        [UIBind] public string HeroArchetype { get; set; } = "Solar Warden";
        [UIBind] public string HeroPower { get; set; } = "1,247,000";
        [UIBind] public string PlayerName { get; set; } = "Player_815d";

        [UIBind] public IList<StageVM> Stages { get; private set; }
        [UIBind] public IList<LeaderboardVM> Leaderboard { get; private set; }
        [UIBind] public IList<HeroStatVM> HeroStats { get; private set; }
        [UIBind] public IList<AbilityVM> Abilities { get; private set; }
        [UIBind] public IList<MasteryNodeVM> MasteryNodes { get; private set; }
        [UIBind] public IList<ChallengeVM> Challenges { get; private set; }
        [UIBind] public IList<UpgradeVM> Upgrades { get; private set; }
        [UIBind] public IList<CurrencyVM> Currencies { get; private set; }
        [UIBind] public StageVM SelectedStage { get; private set; }

        [UIBind] public string MasterySummary => $"{CountUnlockedMasteryNodes()}/{MasteryNodes?.Count ?? 0} unlocked";
        [UIBind] public string ChallengeSummary => $"{CountClaimableChallenges()} ready to claim";
        [UIBind] public string ChallengeCountText => $"{CountCompletedChallenges()} / {Challenges?.Count ?? 0}";
        [UIBind] public string ChallengeResetText => "Daily contracts refresh in 12h. Weekly hunt resets in 3d.";
        [UIBind] public string UpgradeSummary => "Permanent account upgrades shared across every run.";
        [UIBind] public string UpgradeCountText => $"{Upgrades?.Count ?? 0} upgrades";

        [UIBind] public bool IsTabPlay       => currentTab == Tab.Play;
        [UIBind] public bool IsTabMastery    => currentTab == Tab.Mastery;
        [UIBind] public bool IsTabChallenges => currentTab == Tab.Challenges;
        [UIBind] public bool IsTabUpgrades   => currentTab == Tab.Upgrades;

        [UIBind] public bool ShowNameModal { get; private set; }

        [UIElement("name-input")] public Element NameInput;

        enum Tab { Play, Mastery, Challenges, Upgrades }
        Tab currentTab = Tab.Play;
        WevaDocument doc;

        void OnEnable() {
            BuildMockData();
            doc = GetComponent<WevaDocument>();
            if (doc) doc.SetController(this);
        }

        void BuildMockData() {
            Stages = new List<StageVM> {
                new StageVM { Number = 1, Name = "Dunes of Entry",      ObjectivesDone = 1, ObjectivesTotal = 1, IsCompleted = true,
                    Description = "Your descent begins on the windswept surface of the desert planet. Secure the landing zone by defeating the automated Sentinel guarding the perimeter.",
                    Objectives = {
                        new ObjectiveVM { Name = "Defeat Monoculon", Description = "The perimeter guard must be neutralized to secure your foothold on the surface.", Reward = "2. The Sunken Canyon, Razor Burst", IsCompleted = true },
                    },
                },
                new StageVM { Number = 2, Name = "The Sunken Canyon",   ObjectivesDone = 1, ObjectivesTotal = 2,
                    Description = "A geothermal canyon riddled with Razor Burst nests. Survive the gauntlet and clear the lower atrium.",
                    Objectives = {
                        new ObjectiveVM { Name = "Survive 5 minutes", Description = "Hold position against repeated swarm waves.", Reward = "Mastery Shard ×1", IsCompleted = true },
                        new ObjectiveVM { Name = "Defeat 200 Razor Bursts", Description = "Thin the population before the elite tier spawns." },
                    },
                },
                new StageVM { Number = 3, Name = "The Axui Excavation", ObjectivesDone = 0, ObjectivesTotal = 3,
                    Description = "Deep below the surface, an ancient excavation site hums with unstable energy.",
                    Objectives = {
                        new ObjectiveVM { Name = "Reach Layer 3", Description = "Descend past the unstable scaffolding.", Reward = "100 Coins" },
                        new ObjectiveVM { Name = "Defeat the Axui Overseer", Description = "Eliminate the boss guarding the central reactor.", Reward = "Mastery Shard ×2" },
                        new ObjectiveVM { Name = "No deaths", Description = "Complete the stage without dying." },
                    },
                },
                new StageVM { Number = 4, Name = "The Molten Core",     ObjectivesDone = 0, ObjectivesTotal = 2, IsLocked = true,
                    Description = "Locked. Complete the previous stage to access.",
                    Objectives = {
                        new ObjectiveVM { Name = "???", Description = "Hidden until unlocked." },
                        new ObjectiveVM { Name = "???", Description = "Hidden until unlocked." },
                    },
                },
                new StageVM { Number = 5, Name = "Test Arena",          ObjectivesDone = 0, ObjectivesTotal = 0,
                    Description = "Sandbox for testing builds, weapons, and reactions against tunable spawn waves.",
                    Objectives = { },
                },
            };

            Leaderboard = new List<LeaderboardVM> {
                new LeaderboardVM { Rank = 1, Name = "Orvar",    Damage = 12_000_000 },
                new LeaderboardVM { Rank = 2, Name = "X-ing",    Damage =    925_600 },
                new LeaderboardVM { Rank = 3, Name = "Akke",     Damage =    336_300 },
                new LeaderboardVM { Rank = 4, Name = "Chuckiee", Damage =    183_100 },
                new LeaderboardVM { Rank = 5, Name = "Mira",     Damage =     94_800 },
                new LeaderboardVM { Rank = 6, Name = "Pebble",   Damage =     51_200 },
            };

            HeroStats = new List<HeroStatVM> {
                new HeroStatVM { Label = "Total Damage", Value = "12.0M" },
                new HeroStatVM { Label = "Best Stage", Value = "Sunken Canyon" },
                new HeroStatVM { Label = "Win Streak", Value = "7" },
                new HeroStatVM { Label = "Mastery", Value = "46%" },
            };

            Abilities = new List<AbilityVM> {
                new AbilityVM { ShortCode = "SB", Name = "Solar Barrage", Description = "Calls down focused beams on clustered enemies.", Detail = "Equipped · 18s cooldown", IsEquipped = true },
                new AbilityVM { ShortCode = "RW", Name = "Rift Ward", Description = "Drops a temporary shield that slows nearby attackers.", Detail = "Equipped · defense slot", IsEquipped = true },
                new AbilityVM { ShortCode = "OB", Name = "Overburn", Description = "Increases attack speed after collecting energy cells.", Detail = "Passive · rank 3" },
            };

            MasteryNodes = new List<MasteryNodeVM> {
                new MasteryNodeVM { Id = "core-1", Tier = "I",   Name = "Desert Adaptation", Description = "Gain armor and heat resistance on canyon stages.", Rank = 3, MaxRank = 3, BonusText = "+12% armor", IsUnlocked = true },
                new MasteryNodeVM { Id = "core-2", Tier = "II",  Name = "Focused Lens", Description = "Solar Barrage hits one additional target per pulse.", Rank = 2, MaxRank = 4, BonusText = "+1 chain", IsUnlocked = true, IsActive = true },
                new MasteryNodeVM { Id = "core-3", Tier = "III", Name = "Shard Recovery", Description = "Boss objectives have a chance to refund mastery shards.", Rank = 1, MaxRank = 3, BonusText = "+8% refund", IsUnlocked = true },
                new MasteryNodeVM { Id = "core-4", Tier = "IV",  Name = "Axui Resonance", Description = "Unlocks after clearing The Axui Excavation.", Rank = 0, MaxRank = 5, BonusText = "Locked", IsLocked = true },
            };

            Challenges = new List<ChallengeVM> {
                new ChallengeVM { Id = "daily-1", Cadence = "Daily", Name = "Canyon Sweep", Description = "Defeat Razor Bursts in The Sunken Canyon.", Current = 200, Target = 200, Reward = "100 Coins", IsCompleted = true, IsClaimable = true },
                new ChallengeVM { Id = "daily-2", Cadence = "Daily", Name = "No Shield Break", Description = "Finish any stage without losing Rift Ward.", Current = 1, Target = 1, Reward = "Mastery Shard x1", IsCompleted = true },
                new ChallengeVM { Id = "weekly-1", Cadence = "Weekly", Name = "Boss Chain", Description = "Defeat Monoculon and the Axui Overseer this week.", Current = 1, Target = 2, Reward = "Rare Core Cache" },
                new ChallengeVM { Id = "weekly-2", Cadence = "Weekly", Name = "High Voltage", Description = "Deal total damage across all stages.", Current = 7_400_000, Target = 10_000_000, Reward = "500 Coins" },
            };

            Upgrades = new List<UpgradeVM> {
                new UpgradeVM { Id = "atk", ShortCode = "ATK", Name = "Weapon Output", Description = "Raises base weapon damage before ability multipliers.", Level = 6, MaxLevel = 10, Cost = 1200, IsAffordable = true },
                new UpgradeVM { Id = "hp",  ShortCode = "HP",  Name = "Suit Plating", Description = "Adds health and improves recovery pickups.", Level = 4, MaxLevel = 8, Cost = 950, IsAffordable = true },
                new UpgradeVM { Id = "cdr", ShortCode = "CD",  Name = "Ability Cooling", Description = "Reduces active ability cooldowns across every hero.", Level = 2, MaxLevel = 6, Cost = 1800 },
                new UpgradeVM { Id = "eco", ShortCode = "$",   Name = "Salvage Rights", Description = "Increases coin rewards from stages and contracts.", Level = 5, MaxLevel = 5, Cost = 0, IsAffordable = true },
            };

            Currencies = new List<CurrencyVM> {
                new CurrencyVM { Label = "Coins", Value = "3,840" },
                new CurrencyVM { Label = "Mastery Shards", Value = "7" },
                new CurrencyVM { Label = "Core Keys", Value = "2" },
            };

            SelectStage(0);
        }

        void SelectStage(int index) {
            if (index < 0 || index >= Stages.Count) return;
            for (int i = 0; i < Stages.Count; i++) Stages[i].IsSelected = (i == index);
            SelectedStage = Stages[index];
            BumpBindings();
        }

        // --- Event handlers --------------------------------------------------

        public void OnStageClicked(PointerEvent e) {
            var card = e.CurrentTarget;
            if (card == null) return;
            if (!int.TryParse(card.GetAttribute("data-stage-index"), out int idx)) return;
            if (Stages[idx].IsLocked) return;
            SelectStage(idx);
        }

        public void OnPlay() {
            if (SelectedStage == null || SelectedStage.IsLocked) return;
            Debug.Log($"[MainMenu] Play stage {SelectedStage.Number}: {SelectedStage.Name}");
        }

        public void OnTabPlay()       { currentTab = Tab.Play; BumpBindings(); }
        public void OnTabMastery()    { currentTab = Tab.Mastery; BumpBindings(); }
        public void OnTabChallenges() { currentTab = Tab.Challenges; BumpBindings(); }
        public void OnTabUpgrades()   { currentTab = Tab.Upgrades; BumpBindings(); }

        public void OnOpenHeroPicker() => Debug.Log("[MainMenu] Open hero picker");

        public void OnClaimChallenge(PointerEvent e) {
            if (!TryReadIndex(e?.CurrentTarget, "data-challenge-index", out int idx)) return;
            if (Challenges == null || idx < 0 || idx >= Challenges.Count) return;

            var challenge = Challenges[idx];
            if (!challenge.IsClaimable) return;
            challenge.IsClaimable = false;
            challenge.IsCompleted = true;
            challenge.Current = challenge.Target;
            BumpBindings();
            Debug.Log($"[MainMenu] Claim challenge: {challenge.Name}");
        }

        public void OnBuyUpgrade(PointerEvent e) {
            if (!TryReadIndex(e?.CurrentTarget, "data-upgrade-index", out int idx)) return;
            if (Upgrades == null || idx < 0 || idx >= Upgrades.Count) return;

            var upgrade = Upgrades[idx];
            if (upgrade.IsActionDisabled) return;
            upgrade.Level++;
            if (!upgrade.IsMaxed) {
                upgrade.Cost += 350;
                upgrade.IsAffordable = upgrade.Cost <= 1600;
            }
            BumpBindings();
            Debug.Log($"[MainMenu] Buy upgrade: {upgrade.Name} {upgrade.LevelText}");
        }

        public void OnEditName() {
            if (NameInput != null) NameInput.SetAttribute("value", PlayerName ?? "");
            ShowNameModal = true;
            BumpBindings();
        }

        public void OnConfirmName() {
            if (NameInput != null) {
                var v = NameInput.GetAttribute("value");
                if (!string.IsNullOrWhiteSpace(v)) PlayerName = v.Trim();
            }
            ShowNameModal = false;
            BumpBindings();
        }

        public void OnCancelName() { ShowNameModal = false; BumpBindings(); }

        public void OnExit() {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        static bool TryReadIndex(Element element, string attribute, out int index) {
            index = -1;
            return element != null && int.TryParse(element.GetAttribute(attribute), out index);
        }

        int CountUnlockedMasteryNodes() {
            if (MasteryNodes == null) return 0;
            int count = 0;
            for (int i = 0; i < MasteryNodes.Count; i++) if (MasteryNodes[i].IsUnlocked) count++;
            return count;
        }

        int CountCompletedChallenges() {
            if (Challenges == null) return 0;
            int count = 0;
            for (int i = 0; i < Challenges.Count; i++) if (Challenges[i].IsCompleted) count++;
            return count;
        }

        int CountClaimableChallenges() {
            if (Challenges == null) return 0;
            int count = 0;
            for (int i = 0; i < Challenges.Count; i++) if (Challenges[i].IsClaimable) count++;
            return count;
        }
    }

    public sealed class StageVM {
        public int    Number;
        public string Name;
        public string Description;
        public int    ObjectivesDone;
        public int    ObjectivesTotal;
        public bool   IsCompleted;
        public bool   IsLocked;
        public bool   IsSelected;
        public List<ObjectiveVM> Objectives = new();

        public string ProgressText => $"Objectives {ObjectivesDone}/{ObjectivesTotal}";
        public string StatusText   => IsCompleted ? "Completed" : IsLocked ? "Locked" : "Available";
        public string PlayHint     => IsLocked ? "Locked" : IsCompleted ? "Replay stage" : "Begin stage";
    }

    public sealed class ObjectiveVM {
        public string Name;
        public string Description;
        public string Reward = "";
        public bool   IsCompleted;
        public bool   HasNoReward => string.IsNullOrEmpty(Reward);
    }

    public sealed class LeaderboardVM {
        public int    Rank;
        public string Name;
        public long   Damage;
        public bool   IsPodium  => Rank <= 3;
        public string DamageText => FormatBig(Damage);

        static string FormatBig(long n) {
            if (n >= 1_000_000) return $"{n / 1_000_000.0:0.0}M";
            if (n >= 1_000)     return $"{n / 1_000.0:0.0}K";
            return n.ToString();
        }
    }

    public sealed class HeroStatVM {
        public string Label;
        public string Value;
    }

    public sealed class AbilityVM {
        public string ShortCode;
        public string Name;
        public string Description;
        public string Detail;
        public bool   IsEquipped;
    }

    public sealed class MasteryNodeVM {
        public string Id;
        public string Tier;
        public string Name;
        public string Description;
        public int    Rank;
        public int    MaxRank;
        public string BonusText;
        public bool   IsUnlocked;
        public bool   IsActive;
        public bool   IsLocked;

        public string RankText => IsLocked ? "Requires Stage 3" : $"Rank {Rank}/{MaxRank}";
    }

    public sealed class ChallengeVM {
        public string Id;
        public string Cadence;
        public string Name;
        public string Description;
        public int    Current;
        public int    Target;
        public string Reward;
        public bool   IsCompleted;
        public bool   IsClaimable;

        public string ProgressText => IsCompleted ? "Complete" : $"{FormatBig(Current)} / {FormatBig(Target)}";
        public string ActionText => IsClaimable ? "Claim" : IsCompleted ? "Done" : "Track";
        public bool   IsActionDisabled => !IsClaimable;

        static string FormatBig(int n) {
            if (n >= 1_000_000) return $"{n / 1_000_000.0:0.0}M";
            if (n >= 1_000)     return $"{n / 1_000.0:0.0}K";
            return n.ToString();
        }
    }

    public sealed class UpgradeVM {
        public string Id;
        public string ShortCode;
        public string Name;
        public string Description;
        public int    Level;
        public int    MaxLevel;
        public int    Cost;
        public bool   IsAffordable;

        public bool   IsMaxed => Level >= MaxLevel;
        public bool   IsActionDisabled => IsMaxed || !IsAffordable;
        public string LevelText => $"Level {Level}/{MaxLevel}";
        public string ProgressPercent => MaxLevel > 0 ? $"{Level * 100 / MaxLevel}%" : "0%";
        public string CostText => Cost <= 0 ? "Free" : $"{Cost:n0} Coins";
        public string ActionText => IsMaxed ? "Maxed" : IsAffordable ? CostText : "Need Coins";
    }

    public sealed class CurrencyVM {
        public string Label;
        public string Value;
    }
}
