using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Skills;
using NorthIsland.Protocol.Messages.Battle;

namespace NorthIsland.Server.Features.Battles;

public sealed partial class BattleHandlers
{
    /// <summary>Plays a support skill (heal, buff, remover, status) from a fighter on every fighter of its area and sends the action to the battle.</summary>
    /// <param name="session">The session used for logging and for the skill training.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="player">The fighter who uses the skill.</param>
    /// <param name="skill">The skill used.</param>
    /// <param name="target">The fighter it is used on; the skill's area at the user's level spreads around it.</param>
    private async Task SupportAsync(
        GameSession session,
        Battle battle,
        Fighter player,
        SkillRecord skill,
        Fighter target)
    {
        var areaCode = skill.AreaAt(BattleFormulas.SkillLevelOf(player, skill.Id));
        var area = battle.AreaAround(target, areaCode);
        var amount = BattleFormulas.HealAmount(player, skill.Power);
        List<ActionTarget> hits = [];
        List<(Fighter Fighter, FighterStatus Status)> removed = [];
        List<string> whats = [];
        var took = false;
        foreach (var f in area)
        {
            if (player.IsHuman && !f.IsHuman)
                f.NoteAction(player);
            List<FighterValue> values = [];
            var hit = true;
            string what;
            switch (skill.Type)
            {
                case 5 or 16: // remover skills (types 5, 16): take off statuses it removes
                    var gone = f.Statuses.Where(s => StatusRules.IsRemovedBy(s, skill.Effect)).ToList();
                    f.Statuses.RemoveAll(gone.Contains);
                    removed.AddRange(gone.Select(s => (f, s)));
                    what = gone.Count > 0 ? $"removes {string.Join(", ", gone.Select(s => s.Effect))}" : "removes nothing";
                    took |= gone.Count > 0;
                    break;
                case 6 or 7 or 14: // heal skills (types 6, 7, 14): restore SP, HP or both
                    (values, var hp, var sp, var healedTook) = Heal(battle, f, skill, amount);
                    what = $"restores {hp} HP, {sp} SP";
                    took |= healedTook;
                    break;
                default: // other support skills: put a status on the fighter, each enemy rolling its own hit chance
                    var hadEffect = skill.Effect != 0 && f.HasEffect(skill.Effect);
                    var attr = StatusRules.AttrFor(skill.Effect, f);
                    if (player.IsHuman && attr != 0)
                        NoteBurstEffect(player, f, skill.Effect);
                    hit = attr != 0 && !StatusRules.BlockedByDebuff(attr, player, f)
                        && (f.Side == player.Side || GameRandom.Shared.Next(100) < BattleFormulas.StatusHitChancePercent);
                    if (hit)
                    {
                        if (skill.Type is 4 or 19 && player.Kind == 2 && f.Kind == 4 && ReferenceEquals(f.Session, player.Session)) // 2 player, 4 pet
                            player.BuffedPet = true;
                        AddStatus(battle, f, skill, attr, skill.Effect == StatusRules.Poison ? BattleFormulas.PoisonDamage(f) : 0);
                        values.Add(new FighterValue(attr, 0, Negative: false));
                    }
                    what = hit ? $"puts status {skill.Effect} on for {Math.Max((byte)1, skill.Duration)} round(s)" : "misses";
                    took |= hit && !hadEffect;
                    break;
            }
            hits.Add(new ActionTarget(f.Row, f.Col, hit, Guard: 0, values));
            whats.Add($"{f.Name} {what}");
        }
        battle.SendAttackArea(player, areaCode);
        battle.Send(client => BattleMessages.Action(player.Row, player.Col, skill.Id, hits, client));
        if (took)
            TrainSkill(session, player, skill, target.Level);
        foreach (var (f, s) in removed.Where(r => r.Status.Attr is StatusRules.Debuff2 or 227 or 228))
            SendStatusRemoval(battle, f, s);
        logger.LogInformation("[{Session}] {Player} uses {Skill} ({Cost} SP, {Sp} left) on {Target}: {What}", session.Id, player.Name,
            skill.Name, skill.SpCost, player.Sp, target.Name, string.Join("; ", whats));
        await ActionPause(skill.Id, player, area).ConfigureAwait(false);
    }

    /// <summary>Restores a heal skill's SP, HP or both on one fighter, or puts its regeneration on.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="healed">The fighter healed.</param>
    /// <param name="skill">The heal skill (type 6 SP, 7 HP, 14 both).</param>
    /// <param name="amount">The amount the caster heals.</param>
    /// <returns>The values shown on the fighter, the HP and SP restored, and whether the heal counts for skill training.</returns>
    private static (List<FighterValue> Values, int Hp, int Sp, bool Took) Heal(Battle battle, Fighter healed, SkillRecord skill, int amount)
    {
        List<FighterValue> values = [];
        var regeneration = skill.Type == 7 && skill.Effect == StatusRules.Regeneration;
        var hadRegeneration = regeneration && healed.HasEffect(skill.Effect);
        var hp = skill.Type is 7 or 14 ? Math.Min(amount, healed.MaxHp - healed.Hp) : 0;
        var sp = skill.Type is 6 or 14 ? Math.Min(amount, healed.MaxSp - healed.Sp) : 0;
        healed.Hp += hp;
        healed.Sp += sp;
        if (regeneration)
        {
            AddStatus(battle, healed, skill, 232, amount / 2);
            values.Add(new FighterValue(232, hp, Negative: false));
        }
        else
        {
            if (skill.Type is 7 or 14)
                values.Add(new FighterValue(BattleMessages.AttrHp, hp, Negative: false));
            if (skill.Type is 6 or 14)
                values.Add(new FighterValue(BattleMessages.AttrSp, sp, Negative: false));
        }
        return (values, hp, sp, hp + sp > 0 || (regeneration && !hadRegeneration));
    }

    /// <summary>Records a debuff cast on an enemy or a buff put on an ally, for the caster's damage burst.</summary>
    /// <param name="caster">The player or pet who used the status skill.</param>
    /// <param name="target">The fighter it was used on.</param>
    /// <param name="effect">The Skill.dat effect byte.</param>
    private static void NoteBurstEffect(Fighter caster, Fighter target, byte effect)
    {
        if (target.Side != caster.Side)
        {
            if (StatusRules.IsDebuffEffect(effect))
                caster.DebuffsCast.Add(effect);
        }
        else if (!StatusRules.IsDebuffEffect(effect))
            caster.BuffsGiven.Add((effect, target));
    }

    /// <summary>Puts a status on a target, replacing a status that uses the same attribute.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="target">The fighter who gets the status.</param>
    /// <param name="skill">The skill that causes it; its duration sets the last round.</param>
    /// <param name="attr">The status attribute id.</param>
    /// <param name="amount">The value the status carries, such as the poison damage or the regeneration per round.</param>
    private static void AddStatus(
        Battle battle,
        Fighter target,
        SkillRecord skill,
        byte attr,
        int amount)
    {
        foreach (var old in target.Statuses.Where(s => s.Attr == attr).ToList())
        {
            target.Statuses.Remove(old);
            if (old.SkillId != skill.Id)
                SendStatusRemoval(battle, target, old);
        }
        target.Statuses.Add(new FighterStatus
        {
            Attr = attr,
            SkillId = skill.Id,
            Effect = skill.Effect,
            LastRound = battle.Round + Math.Max((byte)1, skill.Duration),
            Amount = amount,
        });
    }

    /// <summary>Tells the battle that a status icon is gone from a fighter.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="f">The fighter.</param>
    /// <param name="s">The status that was removed.</param>
    private static void SendStatusRemoval(Battle battle, Fighter f, FighterStatus s)
    {
        var (kind, sub) = StatusRules.IconOf(s.Attr);
        if (kind != 0)
            battle.Send(BattleMessages.Status(f.Row, f.Col, kind, 0, sub));
    }

    /// <summary>Removes the sleep status from a target that was hit.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="target">The fighter who was hit.</param>
    private static void BreakSleep(Battle battle, Fighter target)
    {
        foreach (var s in target.Statuses.Where(s => s.Effect == StatusRules.Sleep).ToList())
        {
            target.Statuses.Remove(s);
            SendStatusRemoval(battle, target, s);
        }
    }

    /// <summary>Plays the attack of a player or pet: hand attack, or damage skill on every enemy of its area.</summary>
    /// <param name="session">The session used for logging and for the skill training.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="player">The fighter who attacks.</param>
    /// <param name="attack">The command that holds the chosen target.</param>
    /// <param name="skill">The skill used, or null for a hand attack.</param>
    private async Task AttacksAsync(
        GameSession session,
        Battle battle,
        Fighter player,
        BattleCommand attack,
        SkillRecord? skill)
    {
        var target = AttackTargetOf(battle, player, attack);
        if (target is null)
            return;
        var skillId = skill?.Id ?? attack.SkillOrItem;
        var areaCode = skill?.AreaAt(BattleFormulas.SkillLevelOf(player, skill.Id)) ?? 1;
        var area = battle.AreaAround(target, areaCode);
        List<ActionTarget> hits = [];
        List<Fighter> landed = [];
        var gain = 0;
        foreach (var hit in area)
        {
            var (damage, critical, missed) = HitTarget(player, hit, skill, combo: false, area.Count);
            var rider = missed ? null : RiderStatus(battle, player, hit, skill);
            hits.Add(new ActionTarget(hit.Row, hit.Col, Hit: !missed, Guard: 0, BlowValues(damage, critical, missed, rider)));
            if (!missed)
                landed.Add(hit);
            gain += SkillData.BlowExp(hit.Level, hit.MaxHp, damage); // each enemy of the area adds its own share
            logger.LogInformation("[{Session}] {Player} {How} {Monster} for {Damage}{Critical}{Missed}{Rider} ({Hp}/{Max} HP left)", session.Id, player.Name,
                skill is null ? "hits" : $"uses {skill.Name} ({skill.SpCost} SP, {player.Sp} left) on",
                hit.Name,
                damage,
                critical ? " (critical)" : "",
                missed ? " (missed)" : "",
                rider is null ? "" : $" (status {skill!.Effect})", hit.Hp, hit.MaxHp);
        }
        battle.SendAttackArea(player, areaCode);
        battle.Send(client => BattleMessages.Action(player.Row, player.Col, skillId, hits, client));
        TrainSkill(session, player, skill, gain);
        await ActionPause(skillId, player, area).ConfigureAwait(false);
        foreach (var hit in landed)
            BreakSleep(battle, hit);
        foreach (var fallen in area.Where(f => !f.IsAlive))
            await KnockOffAsync(battle, fallen).ConfigureAwait(false);
    }

    /// <summary>Finds the enemy an attack command aims at.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="player">The attacking fighter.</param>
    /// <param name="attack">The attack command.</param>
    /// <returns>The living enemy in the chosen cell, otherwise the first living enemy, or null when none is left.</returns>
    private static Fighter? AttackTargetOf(Battle battle, Fighter player, BattleCommand attack) =>
        battle.At(attack.TargetRow, attack.TargetCol) is { IsAlive: true } chosen && chosen.Side != player.Side
            ? chosen
            : battle.EnemiesOf(player).FirstOrDefault();

    /// <summary>Rolls a player's or pet's blow, takes it off the target's HP and records the kill.</summary>
    /// <param name="player">The attacking fighter.</param>
    /// <param name="target">The fighter hit.</param>
    /// <param name="skill">The skill used, or null for a hand attack.</param>
    /// <param name="combo">True when the blow is part of a combination.</param>
    /// <param name="targets">The number of enemies the attack hits.</param>
    /// <returns>The damage, whether it was a critical hit, and whether it missed.</returns>
    private (int Damage, bool Critical, bool Missed) HitTarget(Fighter player, Fighter target, SkillRecord? skill, bool combo, int targets = 1)
    {
        if (BattleFormulas.IsVoidAgainst(skill, target))
        {
            NoteAction(player, target, 0);
            return (0, false, true);
        }
        int damage;
        bool critical;
        if (options.Value.WindowDamage)
            (damage, critical) = BattleFormulas.WindowBlow(player, target, GameRandom.Shared, skill, options.Value, combo, targets: targets);
        else
        {
            var power = BattleFormulas.PowerTerm(skill, options.Value);
            int raw;
            (raw, critical) = skill switch
            {
                null => BattleFormulas.PhysicalHit(player, target, GameRandom.Shared), // hand attack: physical hit
                { Type: 1 } when power > 0 => BattleFormulas.PhysicalHit(player, target, GameRandom.Shared, power), // skill with a power term: physical hit
                { Type: 1 } => BattleFormulas.PhysicalSkillHit(player, target, GameRandom.Shared), // physical skill without a power term: physical skill hit
                _ => BattleFormulas.SkillHit(player, target, GameRandom.Shared, power, canCritical: true), // any other skill: spell hit that can critical
            };
            damage = BattleFormulas.Scale(raw, BattleFormulas.ElementScale(BattleFormulas.SkillElement(skill) ?? player.Element, target.Element,
                options.Value.ElementAdvantageMultiplier, options.Value.ElementDisadvantageMultiplier)
                * BattleFormulas.DebuffDamageScale(target, options.Value));
            if (combo)
                damage = ComboRules.WithBonus(damage, options.Value);
        }
        if (target.IsInAdminMode)
            damage = 0;
        var wasAlive = target.IsAlive;
        target.Hp = Math.Max(0, target.Hp - damage);
        NoteAction(player, target, damage);
        if (damage > 0)
            player.DealtDamage = true;
        if (wasAlive && !target.IsAlive)
            CreditKill(player, target);
        if (player.HalvesTargetSp && skill is null or { Type: 1 } && target.Sp > 0)
        {
            target.Sp -= target.Sp / 2;
            SendSp(target);
        }
        return (damage, critical, false);
    }

    /// <summary>Rolls the extra status that the Fervidity skill may put on a target it hit.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="attacker">The fighter who used the skill, credited with the status for the damage burst.</param>
    /// <param name="target">The fighter hit.</param>
    /// <param name="skill">The skill used, or null.</param>
    /// <returns>The status value to show in the action, or null when none was put on.</returns>
    private FighterValue? RiderStatus(Battle battle, Fighter attacker, Fighter target, SkillRecord? skill)
    {
        if (skill is not { Id: SkillTable.Fervidity } || !target.IsAlive || GameRandom.Shared.NextDouble() * 100 >= options.Value.FervidityMessPercent
            || StatusRules.AttrFor(skill.Effect, target) is not (> 0 and var attr) || StatusRules.BlockedByDebuff(attr, attacker, target))
            return null;
        NoteBurstEffect(attacker, target, skill.Effect);
        AddStatus(battle, target, skill, attr, 0);
        return new FighterValue(attr, 0, Negative: false);
    }

    /// <summary>Builds the values shown for one blow: the HP lost and the rider status if any.</summary>
    /// <param name="damage">The damage.</param>
    /// <param name="critical">True for a critical hit.</param>
    /// <param name="missed">True when the blow missed, so no HP value is shown.</param>
    /// <param name="rider">The rider status, or null.</param>
    /// <returns>The list of values for the action target.</returns>
    private static List<FighterValue> BlowValues(int damage, bool critical, bool missed, FighterValue? rider)
    {
        List<FighterValue> values = [];
        if (!missed)
            values.Add(new FighterValue(BattleMessages.AttrHp, damage, Negative: true, Critical: critical));
        if (rider is { } status)
            values.Add(status);
        return values;
    }

    /// <summary>Records that an attacker took part in felling a target.</summary>
    /// <param name="attacker">The fighter who delivered the blow.</param>
    /// <param name="target">The fighter who fell.</param>
    private static void CreditKill(Fighter attacker, Fighter target)
    {
        target.KilledBy.Add(attacker);
    }

    /// <summary>Records a player's or pet's action on a monster, for the experience it earns.</summary>
    /// <param name="attacker">The attacking fighter.</param>
    /// <param name="target">The fighter hit.</param>
    /// <param name="damage">The damage done.</param>
    private static void NoteAction(Fighter attacker, Fighter target, int damage)
    {
        if (attacker.IsHuman && !target.IsHuman)
            target.NoteAction(attacker, damage);
    }

    /// <summary>The element wheel factor between two fighters' elements.</summary>
    /// <param name="attacker">The attacking fighter.</param>
    /// <param name="target">The fighter hit.</param>
    /// <returns>The advantage or disadvantage factor from the options, or 1.</returns>
    private double ElementScale(Fighter attacker, Fighter target) =>
        BattleFormulas.ElementScale(attacker.Element, target.Element, options.Value.ElementAdvantageMultiplier, options.Value.ElementDisadvantageMultiplier);

    /// <summary>One attack that may join a combination.</summary>
    /// <param name="Attacker">The attacking fighter.</param>
    /// <param name="Target">The fighter attacked.</param>
    /// <param name="Skill">The skill used, or null for a hand attack.</param>
    /// <param name="SkillId">The id sent in the action record.</param>
    private sealed record ComboAttack(
        Fighter Attacker,
        Fighter Target,
        SkillRecord? Skill,
        ushort SkillId);

    /// <summary>Plays a combination attack when the fighter at a turn and later fighters of its side hit the same target.</summary>
    /// <param name="session">The session used for logging and for the skill training.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="order">The fighters in turn order.</param>
    /// <param name="turn">The index in <paramref name="order"/> of the fighter whose turn it is.</param>
    /// <param name="commands">The commands the players chose.</param>
    /// <param name="monsterPlans">The plans made for the monsters in this round.</param>
    /// <param name="inCombo">The fighters that already acted in a combination this round; the members are added.</param>
    /// <returns>Null when no combination formed, otherwise whether the battle ended.</returns>
    private async Task<bool?> TryComboAsync(
        GameSession session,
        Battle battle,
        List<Fighter> order,
        int turn,
        Dictionary<Fighter, BattleCommand> commands,
        Dictionary<Fighter, MonsterPlan> monsterPlans,
        HashSet<Fighter> inCombo)
    {
        var o = options.Value;
        if (ComboAttackOf(battle, order[turn], commands, monsterPlans) is not { } lead)
            return null;
        double Chance(IReadOnlyCollection<Fighter>? joining = null) =>
            ComboRules.ChancePercent(ComboRules.AverageLevel(battle, lead.Attacker.Side, o, joining), ComboRules.EffectiveLevel(lead.Target, o), o);
        var chance = Chance();
        List<ComboAttack> members = [lead];
        for (var next = turn + 1; next < order.Count; next++)
        {
            var fighter = order[next];
            if (!fighter.IsAlive || fighter.Left || inCombo.Contains(fighter) || fighter.Side != lead.Attacker.Side
                || ComboAttackOf(battle, fighter, commands, monsterPlans) is not { } attack || !ReferenceEquals(attack.Target, lead.Target)
                || !ComboRules.WithinSpeedRange(members[^1].Attacker, fighter, o)
                || o.ComboJoinRule == ComboJoinRule.Level && GameRandom.Shared.NextDouble() * 100 >= (o.ComboOddsLevels == ComboOddsLevels.Members
                    ? Chance([.. members.Select(m => m.Attacker), fighter])
                    : chance))
                continue;
            members.Add(attack);
        }
        if (members.Count < 2)
            return null;

        var target = lead.Target;
        List<ActionRecord> records = [];
        List<(ushort SkillId, Fighter Attacker, Fighter Target)> played = [];
        List<(Fighter Attacker, SkillRecord? Skill, int Gain)> trained = [];
        long comboDamage = 0;
        foreach (var member in members)
        {
            var (attacker, skill) = (member.Attacker, member.Skill);
            inCombo.Add(attacker);
            int damage;
            bool critical, missed = false;
            FighterValue? rider = null;
            if (attacker.IsHuman)
            {
                if (skill is not null)
                    Spend(attacker, skill);
                (damage, critical, missed) = HitTarget(attacker, target, skill, combo: true);
                if (!missed)
                    rider = RiderStatus(battle, attacker, target, skill);
                trained.Add((attacker, skill, SkillData.BlowExp(target.Level, target.MaxHp, damage)));
            }
            else
            {
                if (skill is not null)
                    attacker.Sp -= skill.SpCost;
                (damage, critical) = MonsterBlow(attacker, target, skill, combo: true);
            }
            var guard = !attacker.IsHuman && target.Defending ? (byte)1 : (byte)0;
            records.Add(new ActionRecord(attacker.Row, attacker.Col, member.SkillId,
                [new ActionTarget(target.Row, target.Col, Hit: !missed, guard, BlowValues(damage, critical, missed, rider))]));
            played.Add((member.SkillId, attacker, target));
            comboDamage += damage;
            logger.LogInformation(
                "[{Session}] combo {Number}: {Attacker} {How} {Target} for {Damage}{Critical}{Missed}{Defended} ({Hp}/{Max} HP left)",
                session.Id,
                records.Count,
                attacker.Name, skill is null
                    ? "hits"
                    : $"uses {skill.Name} ({skill.SpCost} SP) on", target.Name, damage, critical ? " (critical)" : "", missed ? " (missed)" : "",
                guard == 1 ? " (defending)" : "", target.Hp, target.MaxHp);
        }
        if (played.Count >= 2 && !target.IsHuman)
            target.ComboDamage += comboDamage;
        if (!target.IsAlive)
        {
            foreach (var member in played.Select(p => p.Attacker).Where(a => a.IsHuman && !target.KilledBy.Contains(a)))
                CreditKill(member, target);
        }
        foreach (var member in members)
            battle.SendAttackArea(member.Attacker);
        battle.Send(client => BattleMessages.Actions(records, client));
        foreach (var (attacker, skill, gain) in trained)
            TrainSkill(session, attacker, skill, gain);
        logger.LogInformation(
            "[{Session}] combination of {Count} on {Target} ({Rule} rule, chance {Chance:0.#} %, average level {Average:0.#})",
            session.Id,
            records.Count,
            target.Name,
            o.ComboJoinRule,
            o.ComboJoinRule == ComboJoinRule.Level ? chance : 100,
            ComboRules.AverageLevel(battle, lead.Attacker.Side, o));
        await ActionPause(played, defends: target.Defending && !lead.Attacker.IsHuman).ConfigureAwait(false);
        BreakSleep(battle, target);
        if (!target.IsAlive)
        {
            if (lead.Attacker.IsHuman)
                await KnockOffAsync(battle, target).ConfigureAwait(false);
            else
            {
                target.Statuses.Clear();
                logger.LogInformation("[{Session}] {What} {Name} is down", session.Id, target.OwnedPet is null ? "player" : "pet", target.Name);
            }
        }
        return TryFinish(session, battle);
    }

    /// <summary>Finds the single-target attack a fighter would make, if any.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="fighter">The fighter.</param>
    /// <param name="commands">The commands the players chose.</param>
    /// <param name="monsterPlans">The plans made for the monsters; a missing or stale plan is replaced.</param>
    /// <returns>The attack, or null when the fighter cannot act or does not make a single-target attack.</returns>
    private ComboAttack? ComboAttackOf(
        Battle battle,
        Fighter fighter,
        Dictionary<Fighter, BattleCommand> commands,
        Dictionary<Fighter, MonsterPlan> monsterPlans)
    {
        if (StatusRules.CannotAct(fighter))
            return null;
        if (fighter.IsHuman)
        {
            if (!commands.TryGetValue(fighter, out var command) || command.IsItem
                || command.SkillOrItem is SkillTable.Defense or SkillTable.Flee or SkillTable.Capture)
                return null;
            if (SkillTable.IsNormalAttack(command.SkillOrItem) || (fighter.Session is not null && IsPetNormalAttack(fighter, command.SkillOrItem)))
                return AttackTargetOf(battle, fighter, command) is { } enemy ? new ComboAttack(fighter, enemy, null, command.SkillOrItem) : null;
            return CastRefusal(battle, fighter, command) is (null, { } skill, { } target)
                && ComboRules.IsSingleTargetAttack(skill, BattleFormulas.SkillLevelOf(fighter, skill.Id))
                ? new ComboAttack(fighter, target, skill, skill.Id)
                : null;
        }
        if (!monsterPlans.TryGetValue(fighter, out var plan) || (plan.SupportTarget is null && !plan.Target.IsAlive))
        {
            if (PlanMonster(battle, fighter) is not { } fresh)
                return null;
            monsterPlans[fighter] = plan = fresh;
        }
        return plan.SupportTarget is null && ComboRules.IsSingleTargetAttack(plan.Skill, plan.Skill is null ? 1 : BattleFormulas.SkillLevelOf(fighter, plan.Skill.Id))
            ? new ComboAttack(fighter, plan.Target, plan.Skill, plan.Skill?.Id ?? SkillTable.HandAttack)
            : null;
    }

    /// <summary>Plays a monster's turn: a support skill on a monster or an attack on a player or pet.</summary>
    /// <param name="session">The session used for logging.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="monster">The monster.</param>
    /// <param name="plan">The plan made for it earlier in the round, or null to make a new one.</param>
    private async Task MonsterAttacksAsync(GameSession session, Battle battle, Fighter monster, MonsterPlan? plan = null)
    {
        if (plan is null || (plan.SupportTarget is null && !plan.Target.IsAlive))
            plan = PlanMonster(battle, monster);
        if (plan is null)
            return;
        var (player, skill, supportTarget) = plan;
        if (skill is not null && supportTarget is not null)
        {
            monster.Sp -= skill.SpCost;
            await SupportAsync(session, battle, monster, skill, supportTarget).ConfigureAwait(false);
            return;
        }
        var skillId = skill?.Id ?? SkillTable.HandAttack;
        if (skill is not null)
            monster.Sp -= skill.SpCost;
        var areaCode = skill?.AreaAt(BattleFormulas.SkillLevelOf(monster, skill.Id)) ?? 1;
        var area = battle.AreaAround(player, areaCode);
        List<ActionTarget> hits = [];
        foreach (var hit in area)
        {
            var (damage, critical) = MonsterBlow(monster, hit, skill, combo: false, area.Count);
            hits.Add(new ActionTarget(hit.Row, hit.Col, Hit: true, Guard: hit.Defending ? (byte)1 : (byte)0,
                [new FighterValue(BattleMessages.AttrHp, damage, Negative: true, Critical: critical)]));
            logger.LogInformation("[{Session}] {Monster} {How} {Player} for {Damage}{Critical}{Defended} ({Hp}/{Max} HP left)", session.Id, monster.Name,
                skill is null ? "hits" : $"uses {skill.Name} ({skill.SpCost} SP) on", hit.Name, damage, critical ? " (critical)" : "",
                hit.Defending ? " (defending)" : "", hit.Hp, hit.MaxHp);
        }
        battle.SendAttackArea(monster, areaCode);
        battle.Send(client => BattleMessages.Action(monster.Row, monster.Col, skillId, hits, client));
        await ActionPause(skillId, monster, area, defends: area.Exists(f => f.Defending)).ConfigureAwait(false);
        foreach (var hit in area)
        {
            BreakSleep(battle, hit);
            if (hit.IsAlive)
                continue;
            hit.Statuses.Clear();
            logger.LogInformation("[{Session}] {What} {Name} is down", session.Id, hit.OwnedPet is null ? "player" : "pet", hit.Name);
        }
    }

    /// <summary>What a monster is going to do this round.</summary>
    /// <param name="Target">The player or pet it attacks.</param>
    /// <param name="Skill">The skill it uses, or null for a hand attack.</param>
    /// <param name="SupportTarget">The monster a support skill is used on, or null when it attacks.</param>
    private sealed record MonsterPlan(
        Fighter Target,
        SkillRecord? Skill,
        Fighter? SupportTarget);

    /// <summary>Chooses a random living player or pet as the target and a skill for a monster.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="monster">The monster.</param>
    /// <returns>The plan, or null when nobody on the player's side is alive.</returns>
    private MonsterPlan? PlanMonster(Battle battle, Fighter monster)
    {
        var targets = battle.OwnSide.Where(f => f.IsAlive).ToList();
        if (targets.Count == 0)
            return null;
        var player = targets[GameRandom.Shared.Next(targets.Count)];
        var (skill, supportTarget) = ChooseMonsterSkill(battle, monster);
        return new MonsterPlan(player, skill, supportTarget);
    }

    /// <summary>Rolls a monster's blow, takes it off the player's or pet's HP and applies the defend halving.</summary>
    /// <param name="monster">The attacking monster.</param>
    /// <param name="player">The player or pet hit.</param>
    /// <param name="skill">The skill used, or null for a hand attack.</param>
    /// <param name="combo">True when the blow is part of a combination.</param>
    /// <param name="targets">The number of fighters the attack hits.</param>
    /// <returns>The damage and whether it was a critical hit.</returns>
    private (int Damage, bool Critical) MonsterBlow(Fighter monster, Fighter player, SkillRecord? skill, bool combo, int targets = 1)
    {
        int raw;
        bool critical;
        if (options.Value.WindowDamage)
            (raw, critical) = BattleFormulas.WindowBlow(
                monster,
                player,
                GameRandom.Shared,
                skill,
                options.Value,
                combo,
                Math.Max(0, options.Value.MonsterDamageScale),
                player.Defending,
                targets);
        else
        {
            var (formula, crit) = skill is null
                ? BattleFormulas.PhysicalHit(monster, player, GameRandom.Shared)
                : BattleFormulas.SkillHit(monster, player, GameRandom.Shared, 0, canCritical: true);
            critical = crit;
            raw = (int)Math.Min(
                int.MaxValue,
                BattleFormulas.Scale(formula, ElementScale(monster, player) * BattleFormulas.DebuffDamageScale(player, options.Value))
                    * Math.Max(0, options.Value.MonsterDamageScale));
            if (player.Defending)
                raw = BattleFormulas.Defended(raw);
            if (combo)
                raw = ComboRules.WithBonus(raw, options.Value);
        }
        if (player.IsInAdminMode)
            raw = 0;
        if (raw > 2L * player.MaxHp && player.Kind == 2) // player
            player.SentBack = true;
        player.Hp = Math.Max(0, player.Hp - raw);
        return (raw, critical);
    }

    /// <summary>Picks the skill a monster uses this turn, if any, and the monster it is used on.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="monster">The monster.</param>
    /// <returns>The skill, and for a support skill its target; both null when the monster uses a hand attack.</returns>
    private (SkillRecord? Skill, Fighter? SupportTarget) ChooseMonsterSkill(Battle battle, Fighter monster)
    {
        if (monster.Npc is not { Skills.Count: > 0 } npc || GameRandom.Shared.Next(100) >= BattleFormulas.MonsterSkillChancePercent)
            return (null, null);
        var allies = battle.Monsters.Where(m => m.IsAlive).ToList();
        var enemies = battle.OwnSide.Where(f => f.IsAlive).ToList();
        List<(SkillRecord Skill, Fighter? Target)> usable = [];
        foreach (var s in npc.Skills.Select(id => skills[id]).OfType<SkillRecord>())
        {
            if (s.SpCost > monster.Sp || !PlayableTypes.Contains(s.Type))
                continue;
            Fighter? target = s.Type switch
            {
                1 or 2 => null, // damage skills: no support target
                3 or 15 or 22 => Pick(enemies.Where(f => !f.HasEffect(s.Effect) && StatusRules.AttrFor(s.Effect, f) is > 0 and var a
                    && !StatusRules.BlockedByDebuff(a, monster, f))), // status skill: a player-side target it can land on
                4 or 19 => s.TargetType == 3
                    ? (monster.HasEffect(s.Effect) ? null : monster)
                    : Pick(allies.Where(m => !m.HasEffect(s.Effect) && StatusRules.AttrFor(s.Effect, m) != 0)), // buff or shield: itself or an ally
                6 => Pick(allies.Where(m => m.Sp * 2 < m.MaxSp)), // SP heal: an ally below half SP
                7 or 14 => Pick(allies.Where(m => m.Hp * 2 < m.MaxHp)), // HP heal: an ally below half HP
                5 => Pick(allies.Where(m => m.Statuses.Exists(st => StatusRules.IsRemovedBy(st, s.Effect)))), // own-side remover: ally with the status
                16 => Pick(enemies.Where(f => f.Statuses.Exists(st => StatusRules.IsRemovedBy(st, s.Effect)))), // other-side remover: enemy with the status
                _ => null, // any other type: no target
            };
            if (s.Type is 1 or 2 || target is not null)
                usable.Add((s, target));
        }
        if (usable.Count == 0)
            return (null, null);
        return usable[GameRandom.Shared.Next(usable.Count)];

        static Fighter? Pick(IEnumerable<Fighter> candidates)
        {
            var list = candidates.ToList();
            return list.Count == 0 ? null : list[GameRandom.Shared.Next(list.Count)];
        }
    }
}
