﻿namespace Valvrave_Sharp.Plugin
{
    #region

    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Windows.Forms;

    using LeagueSharp;
    using LeagueSharp.SDK;
    using LeagueSharp.SDK.Core.UI.IMenu.Values;
    using LeagueSharp.SDK.Core.Utils;
    using LeagueSharp.SDK.Core.Wrappers.Damages;
    using LeagueSharp.SDK.Modes;

    using SharpDX;

    using Valvrave_Sharp.Core;
    using Valvrave_Sharp.Evade;

    using Color = System.Drawing.Color;
    using Menu = LeagueSharp.SDK.Core.UI.IMenu.Menu;
    using Skillshot = Valvrave_Sharp.Evade.Skillshot;

    #endregion

    internal class Zed : Program
    {
        #region Static Fields

        private static GameObject deathMark;

        private static int lastW;

        private static Spell spellQ, spellW;

        private static bool wCasted, rCasted;

        private static MissileClient wMissile;

        private static Obj_AI_Minion wShadow, rShadow;

        private static int wShadowT, rShadowT;

        #endregion

        #region Constructors and Destructors

        public Zed()
        {
            Q = new Spell(SpellSlot.Q, 925).SetSkillshot(0.275f, 50, 1700, true, SkillshotType.SkillshotLine);
            Q2 = new Spell(Q.Slot, Q.Range).SetSkillshot(Q.Delay, Q.Width, Q.Speed, true, Q.Type);
            spellQ = new Spell(Q.Slot, Q.Range).SetSkillshot(Q.Delay, Q.Width, Q.Speed, true, Q.Type);
            W = new Spell(SpellSlot.W, 700).SetSkillshot(0, 40, 1750, false, Q.Type);
            spellW = new Spell(W.Slot).SetSkillshot(W.Delay, W.Width, W.Speed, false, Q.Type);
            E = new Spell(SpellSlot.E, 290).SetTargetted(0.005f, float.MaxValue);
            R = new Spell(SpellSlot.R, 625);
            Q.DamageType = W.DamageType = E.DamageType = R.DamageType = DamageType.Physical;
            Q.MinHitChance = HitChance.VeryHigh;

            var comboMenu = MainMenu.Add(new Menu("Combo", "Combo"));
            {
                comboMenu.Separator("Q/E: Always On");
                comboMenu.Bool("Ignite", "Use Ignite");
                comboMenu.Bool("Item", "Use Item");
                comboMenu.Separator("Swap Settings");
                comboMenu.Bool("SwapIfKill", "Swap W/R If Mark Can Kill Target", false);
                comboMenu.Slider("SwapIfHpU", "Swap W/R If Hp < (%)", 10);
                comboMenu.List("SwapGap", "Swap W/R To Gap Close", new[] { "OFF", "Smart", "Always" }, 1);
                comboMenu.Separator("W Settings");
                comboMenu.Bool("WNormal", "Use For Non-R Combo");
                comboMenu.List("WAdv", "Use For R Combo", new[] { "OFF", "Line", "Triangle", "Mouse" }, 1);
                comboMenu.Separator("R Settings");
                comboMenu.KeyBind("R", "Use R", Keys.X, KeyBindType.Toggle);
                comboMenu.List("RMode", "Mode", new[] { "Always", "Wait Q/E" });
                comboMenu.Slider(
                    "RStopRange",
                    "Prevent Q/W/E If R Ready And Distance <=",
                    (int)(R.Range + 200),
                    (int)R.Range,
                    (int)(R.Range + W.Range));
                comboMenu.Separator("Extra R Settings");
                GameObjects.EnemyHeroes.ForEach(
                    i => comboMenu.Bool("RCast" + i.ChampionName, "Cast On " + i.ChampionName, false));
            }
            var hybridMenu = MainMenu.Add(new Menu("Hybrid", "Hybrid"));
            {
                hybridMenu.List("Mode", "Mode", new[] { "W-E-Q", "E-Q", "Q" });
                hybridMenu.Separator("Auto Q Settings (Champ)");
                hybridMenu.KeyBind("AutoQ", "KeyBind", Keys.T, KeyBindType.Toggle);
                hybridMenu.Slider("AutoQMpA", "If Mp >=", 100, 0, 200);
                hybridMenu.Separator("Auto E Settings (Champ/Shadow)");
                hybridMenu.Bool("AutoE", "Auto", false);
            }
            var lhMenu = MainMenu.Add(new Menu("LastHit", "Last Hit"));
            {
                lhMenu.Bool("Q", "Use Q");
            }
            var ksMenu = MainMenu.Add(new Menu("KillSteal", "Kill Steal"));
            {
                ksMenu.Bool("Q", "Use Q");
                ksMenu.Bool("E", "Use E");
            }
            if (GameObjects.EnemyHeroes.Any())
            {
                Evade.Init();
                EvadeTarget.Init();
            }
            var drawMenu = MainMenu.Add(new Menu("Draw", "Draw"));
            {
                drawMenu.Bool("Q", "Q Range", false);
                drawMenu.Bool("W", "W Range", false);
                drawMenu.Bool("E", "E Range", false);
                drawMenu.Bool("R", "R Range", false);
                drawMenu.Bool("RStop", "Prevent Q/W/E Range", false);
                drawMenu.Bool("UseR", "R In Combo Status");
                drawMenu.Bool("Target", "Target");
                drawMenu.Bool("DMark", "Death Mark");
                drawMenu.Bool("WPos", "W Shadow");
                drawMenu.Bool("RPos", "R Shadow");
            }
            MainMenu.KeyBind("FleeW", "Use W To Flee", Keys.C);

            Evade.Evading += Evading;
            Evade.TryEvading += TryEvading;
            Game.OnUpdate += OnUpdate;
            Drawing.OnDraw += OnDraw;
            Obj_AI_Base.OnProcessSpellCast += (sender, args) =>
                {
                    if (!sender.IsMe || args.Slot != SpellSlot.R || args.SData.Name != "ZedR")
                    {
                        return;
                    }
                    wCasted = false;
                    rCasted = true;
                };
            Spellbook.OnCastSpell += (sender, args) =>
                {
                    if (!sender.Owner.IsMe || args.Slot != SpellSlot.W || !IsWOne)
                    {
                        return;
                    }
                    rCasted = false;
                    wCasted = true;
                };
            GameObject.OnCreate += (sender, args) =>
                {
                    if (sender.IsEnemy || sender.Type != GameObjectType.obj_AI_Minion)
                    {
                        return;
                    }
                    var shadow = sender as Obj_AI_Minion;
                    if (shadow == null || shadow.CharData.BaseSkinName != "zedshadow")
                    {
                        return;
                    }
                    if (wCasted)
                    {
                        wShadowT = Variables.TickCount;
                        wShadow = shadow;
                        wCasted = rCasted = false;
                    }
                    else if (rCasted)
                    {
                        rShadowT = Variables.TickCount;
                        rShadow = shadow;
                        wCasted = rCasted = false;
                    }
                };
            Obj_AI_Base.OnBuffAdd += (sender, args) =>
                {
                    if (sender.IsEnemy || sender.Type != GameObjectType.obj_AI_Minion || !args.Buff.Caster.IsMe)
                    {
                        return;
                    }
                    var shadow = sender as Obj_AI_Minion;
                    if (shadow == null || shadow.CharData.BaseSkinName != "zedshadow")
                    {
                        return;
                    }
                    switch (args.Buff.Name)
                    {
                        case "zedwshadowbuff":
                            if (wShadow.Compare(shadow))
                            {
                                return;
                            }
                            wShadowT = Variables.TickCount;
                            wShadow = shadow;
                            break;
                        case "zedrshadowbuff":
                            if (rShadow.Compare(shadow))
                            {
                                return;
                            }
                            rShadowT = Variables.TickCount;
                            rShadow = shadow;
                            break;
                    }
                };
            Obj_AI_Base.OnPlayAnimation += (sender, args) =>
                {
                    if (sender.IsEnemy || sender.Type != GameObjectType.obj_AI_Minion || args.Animation != "Death")
                    {
                        return;
                    }
                    if (sender.Compare(wShadow))
                    {
                        wShadow = null;
                    }
                    else if (sender.Compare(rShadow))
                    {
                        rShadow = null;
                    }
                };
            GameObject.OnCreate += (sender, args) =>
                {
                    var missile = sender as MissileClient;
                    if (missile != null && missile.SpellCaster.IsMe && missile.SData.Name == "ZedWMissile")
                    {
                        wMissile = missile;
                        return;
                    }
                    if (sender.Name != "Zed_Base_R_buf_tell.troy")
                    {
                        return;
                    }
                    var target = GameObjects.EnemyHeroes.FirstOrDefault(i => i.IsValidTarget() && HaveR(i));
                    if (target != null && target.Distance(sender) < 150)
                    {
                        deathMark = sender;
                    }
                };
            GameObject.OnDelete += (sender, args) =>
                {
                    if (sender.Compare(wMissile))
                    {
                        wMissile = null;
                    }
                    else if (sender.Compare(deathMark))
                    {
                        deathMark = null;
                    }
                };
            Spellbook.OnCastSpell += (sender, args) =>
                {
                    if (!sender.Owner.IsMe || args.Slot != SpellSlot.W || !IsWOne)
                    {
                        return;
                    }
                    lastW = Variables.TickCount;
                };
        }

        #endregion

        #region Properties

        private static bool CanR
            =>
                MainMenu["Combo"]["RMode"].GetValue<MenuList>().Index == 0
                || (Q.IsReady(500) && Player.Mana >= Q.Instance.ManaCost - 10)
                || (E.IsReady(500) && Player.Mana >= E.Instance.ManaCost - 10);

        private static Obj_AI_Hero GetTarget
        {
            get
            {
                var extraRange = RangeTarget;
                if (Q.IsReady())
                {
                    extraRange += Q.Width / 2;
                }
                if (Variables.Orbwalker.GetActiveMode() == OrbwalkingMode.Combo
                    && MainMenu["Combo"]["R"].GetValue<MenuKeyBind>().Active && RState == 0)
                {
                    var targetR =
                        Variables.TargetSelector.GetTargets(Q.Range + extraRange, Q.DamageType)
                            .OrderByDescending(i => new Priority().GetDefaultPriority(i))
                            .FirstOrDefault(i => MainMenu["Combo"]["RCast" + i.ChampionName]);
                    if (targetR != null)
                    {
                        return targetR;
                    }
                }
                var targets = Variables.TargetSelector.GetTargets(Q.Range + extraRange, Q.DamageType, false);
                if (targets.Count == 0)
                {
                    return null;
                }
                var target = targets.FirstOrDefault(HaveR);
                return target != null
                           ? (IsKillByMark(target)
                                  ? (targets.FirstOrDefault(i => !i.Compare(target)) ?? target)
                                  : target)
                           : targets.FirstOrDefault();
            }
        }

        private static bool IsCastingW => !wShadow.IsValid() && wMissile != null && Player.Distance(wMissile) > 300;

        private static bool IsROne => R.Instance.SData.Name == "ZedR";

        private static bool IsWOne => W.Instance.SData.Name == "ZedW";

        private static float RangeTarget
        {
            get
            {
                var validW = wShadow.IsValid();
                var validR = rShadow.IsValid();
                var posW = validW ? wShadow.ServerPosition : new Vector3();
                if (!posW.IsValid() && IsCastingW)
                {
                    validW = true;
                    posW = wMissile.EndPosition;
                }
                return validW && validR
                           ? Math.Max(rShadow.DistanceToPlayer(), posW.DistanceToPlayer())
                           : (WState == 0 && Variables.TickCount - lastW > 150
                                  ? (validR ? Math.Max(rShadow.DistanceToPlayer(), 600) : 600)
                                  : (validW ? posW.DistanceToPlayer() : (validR ? rShadow.DistanceToPlayer() : 0)));
            }
        }

        private static bool RShadowCanQ
            => rShadow.IsValid() && Variables.TickCount - rShadowT <= 7500 - Q.Delay * 1000 + 50;

        private static int RState => R.IsReady() ? (IsROne ? 0 : 1) : (IsROne ? -1 : 2);

        private static bool WShadowCanQ
            => wShadow.IsValid() && Variables.TickCount - wShadowT <= 4500 - Q.Delay * 1000 + 50;

        private static int WState => W.IsReady() ? (IsWOne ? 0 : 1) : -1;

        #endregion

        #region Methods

        private static void AutoQ()
        {
            if (!Q.IsReady() || !MainMenu["Hybrid"]["AutoQ"].GetValue<MenuKeyBind>().Active
                || Player.Mana < MainMenu["Hybrid"]["AutoQMpA"])
            {
                return;
            }
            var target = Q.GetTarget(Q.Width / 2);
            if (target == null)
            {
                return;
            }
            var pred = Q.VPrediction(target, true, CollisionableObjects.YasuoWall);
            if (pred.Hitchance >= Q.MinHitChance)
            {
                Q.Cast(pred.CastPosition);
            }
        }

        private static SpellSlot CanW(Obj_AI_Hero target, float dist = -1)
        {
            if (Q.IsReady() && Player.Mana >= Q.Instance.ManaCost + W.Instance.ManaCost
                && (dist > -1 ? dist : target.DistanceToPlayer()) < W.Range - 100 + Q.Range)
            {
                return SpellSlot.Q;
            }
            if (E.IsReady() && Player.Mana >= E.Instance.ManaCost + W.Instance.ManaCost
                && (dist > -1 ? dist : target.DistanceToPlayer()) < W.Range + E.Range)
            {
                return SpellSlot.E;
            }
            return SpellSlot.Unknown;
        }

        private static void CastE(bool onlyKill = false)
        {
            if (!E.IsReady())
            {
                return;
            }
            var targets = Variables.TargetSelector.GetTargets(E.Range + 20 + RangeTarget, E.DamageType, onlyKill);
            if (onlyKill)
            {
                targets = targets.Where(i => !IsKillByMark(i) && i.Health + i.PhysicalShield <= E.GetDamage(i)).ToList();
            }
            if (targets.Count == 0)
            {
                return;
            }
            if (targets.Any(IsInRangeE))
            {
                E.Cast();
            }
        }

        private static void CastQ(Obj_AI_Hero target)
        {
            if (!Q.IsReady())
            {
                return;
            }
            Q2.UpdateSourcePosition();
            var pPred = Q2.VPrediction(target, true, CollisionableObjects.YasuoWall);
            Prediction.PredictionOutput wPred = null;
            if (WShadowCanQ)
            {
                Q2.UpdateSourcePosition(wShadow.ServerPosition, wShadow.ServerPosition);
                wPred = Q2.VPrediction(target, true, CollisionableObjects.YasuoWall, false);
            }
            else if (IsCastingW)
            {
                Q2.UpdateSourcePosition(wMissile.EndPosition, wMissile.EndPosition);
                wPred = Q2.VPrediction(target, true, CollisionableObjects.YasuoWall, false);
            }
            Prediction.PredictionOutput rPred = null;
            if (RShadowCanQ)
            {
                Q2.UpdateSourcePosition(rShadow.ServerPosition, rShadow.ServerPosition);
                rPred = Q2.VPrediction(target, true, CollisionableObjects.YasuoWall, false);
            }
            if (wPred != null && wPred.Hitchance >= Q.MinHitChance)
            {
                Q.Cast(wPred.CastPosition);
            }
            else if (rPred != null && rPred.Hitchance >= Q.MinHitChance)
            {
                Q.Cast(rPred.CastPosition);
            }
            else if (pPred.Hitchance >= Q.MinHitChance)
            {
                Q.Cast(pPred.CastPosition);
            }
        }

        private static bool CastQKill(Spell spell, Obj_AI_Base target, bool checkMove = true)
        {
            var pred = spell.VPrediction(target, true, CollisionableObjects.YasuoWall, checkMove);
            if (pred.Hitchance < Q.MinHitChance)
            {
                return false;
            }
            var col = pred.VCollision(CollisionableObjects.Heroes | CollisionableObjects.Minions);
            return col.Count == 0
                       ? Q.Cast(pred.CastPosition)
                       : (target.Type == GameObjectType.obj_AI_Hero
                              ? target.Health + target.PhysicalShield
                              : spell.GetHealthPrediction(target)) <= Q.GetDamage(target, Damage.DamageStage.SecondForm)
                         && Q.Cast(pred.CastPosition);
        }

        private static void CastW(Obj_AI_Hero target, SpellSlot slot, bool isRCombo = false)
        {
            if (slot == SpellSlot.Unknown || Variables.TickCount - lastW <= 1000)
            {
                return;
            }
            if (slot == SpellSlot.Q)
            {
                spellW.Range = Q.Range;
                spellW.Delay = Q.Delay;
                spellW.Width = Q.Width;
                spellW.Speed = Q.Speed;
                if (!Q.IsInRange(target))
                {
                    spellW.Range += W.Range;
                    spellW.Delay += W.Delay + (W.Range / W.Speed);
                }
            }
            else
            {
                spellW.Range = W.Range;
                spellW.Delay = W.Delay;
                spellW.Width = W.Width;
                spellW.Speed = W.Speed;
                if (!W.IsInRange(target))
                {
                    spellW.Range += E.Range;
                    spellW.Delay += W.Delay + (W.Range / W.Speed);
                }
            }
            var pred = spellW.VPrediction(target, true);
            if (pred.Hitchance < HitChance.High)
            {
                return;
            }
            var posPlayer = Player.ServerPosition.ToVector2();
            var posPred = pred.UnitPosition.ToVector2();
            var posCast = pred.CastPosition.ToVector2();
            if (posPlayer.Distance(posPred) < 550)
            {
                posCast = posPlayer.Extend(posPred, 550);
            }
            if (isRCombo)
            {
                var posShadowR = rShadow.ServerPosition.ToVector2();
                var rangePlaceW = posPlayer.Distance(posPred) < 550 ? 550 : W.Range;
                switch (MainMenu["Combo"]["WAdv"].GetValue<MenuList>().Index)
                {
                    case 1:
                        posCast = posPlayer + (posPred - posShadowR).Normalized() * rangePlaceW;
                        break;
                    case 2:
                        var subPos1 = posPlayer + (posPred - posShadowR).Normalized().Perpendicular() * rangePlaceW;
                        var subPos2 = posPlayer + (posShadowR - posPred).Normalized().Perpendicular() * rangePlaceW;
                        if (!subPos1.IsWall() && subPos2.IsWall())
                        {
                            posCast = subPos1;
                        }
                        else if (subPos1.IsWall() && !subPos2.IsWall())
                        {
                            posCast = subPos2;
                        }
                        else
                        {
                            posCast = subPos1.CountEnemyHeroesInRange(500) > subPos2.CountEnemyHeroesInRange(500)
                                          ? subPos1
                                          : subPos2;
                        }
                        break;
                    case 3:
                        posCast = Game.CursorPos.ToVector2();
                        break;
                }
            }
            W.Cast(posCast);
        }

        private static void Combo()
        {
            var target = GetTarget;
            if (target != null)
            {
                Swap(target);
                var useR = MainMenu["Combo"]["R"].GetValue<MenuKeyBind>().Active;
                var targetR = MainMenu["Combo"]["RCast" + target.ChampionName];
                var stateR = RState;
                var canCast = !useR || !targetR
                              || (stateR == 0 && target.DistanceToPlayer() > MainMenu["Combo"]["RStopRange"])
                              || stateR == -1;
                if (stateR == 0 && useR && targetR && R.IsInRange(target) && CanR && R.CastOnUnit(target))
                {
                    return;
                }
                if (MainMenu["Combo"]["Ignite"] && Ignite.IsReady() && (HaveR(target) || target.HealthPercent < 25)
                    && target.DistanceToPlayer() < IgniteRange)
                {
                    Player.Spellbook.CastSpell(Ignite, target);
                }
                var norW = MainMenu["Combo"]["WNormal"];
                var advW = MainMenu["Combo"]["WAdv"].GetValue<MenuList>().Index;
                if ((norW || advW > 0) && WState == 0)
                {
                    var slot = CanW(target);
                    if (slot != SpellSlot.Unknown)
                    {
                        if (advW > 0 && rShadow.IsValid() && useR && targetR && HaveR(target) && !IsKillByMark(target))
                        {
                            CastW(target, slot, true);
                            return;
                        }
                        if (norW)
                        {
                            if (stateR < 1 && canCast)
                            {
                                CastW(target, slot);
                            }
                            if (rShadow.IsValid() && useR && targetR && !HaveR(target))
                            {
                                CastW(target, slot);
                            }
                        }
                    }
                    else if (target.Health + target.PhysicalShield <= Player.GetAutoAttackDamage(target)
                             && !E.IsInRange(target) && !IsKillByMark(target)
                             && target.DistanceToPlayer() < W.Range + target.GetRealAutoAttackRange() - 100
                             && W.Cast(target.ServerPosition.ToVector2().Extend(Player.ServerPosition, -100)))
                    {
                        return;
                    }
                }
                if (canCast || rShadow.IsValid())
                {
                    CastE();
                    CastQ(target);
                }
            }
            if (MainMenu["Combo"]["Item"])
            {
                UseItem(target);
            }
        }

        private static void Evading(Obj_AI_Base sender)
        {
            var skillshot =
                Evade.SkillshotAboutToHit(sender, 50)
                    .Where(i => i.CanDodge)
                    .OrderByDescending(i => i.DangerLevel)
                    .ToList();
            if (skillshot.Count == 0)
            {
                return;
            }
            var zedW2 = EvadeSpellDatabase.Spells.FirstOrDefault(i => i.Enable && i.IsReady && i.Slot == SpellSlot.W);
            if (zedW2 != null && wShadow.IsValid() && !Evade.IsAboutToHit(wShadow, 30)
                && (!wShadow.IsUnderEnemyTurret() || MainMenu["Evade"]["Spells"][zedW2.Name]["WTower"])
                && skillshot.Any(i => i.DangerLevel >= zedW2.DangerLevel))
            {
                sender.Spellbook.CastSpell(zedW2.Slot);
                return;
            }
            var zedR2 =
                EvadeSpellDatabase.Spells.FirstOrDefault(
                    i => i.Enable && i.IsReady && i.Slot == SpellSlot.R && i.CheckSpellName == "zedr2");
            if (zedR2 != null && rShadow.IsValid() && !Evade.IsAboutToHit(rShadow, 30)
                && (!rShadow.IsUnderEnemyTurret() || MainMenu["Evade"]["Spells"][zedR2.Name]["RTower"])
                && skillshot.Any(i => i.DangerLevel >= zedR2.DangerLevel))
            {
                sender.Spellbook.CastSpell(zedR2.Slot);
            }
        }

        private static List<double> GetCombo(Obj_AI_Hero target, bool useQ, bool useW, bool useE)
        {
            var dmgTotal = 0d;
            var manaTotal = 0f;
            if (MainMenu["Combo"]["Item"])
            {
                if (Bilgewater.IsReady)
                {
                    dmgTotal += Player.CalculateDamage(target, DamageType.Magical, 100);
                }
                if (BotRuinedKing.IsReady)
                {
                    dmgTotal += Player.CalculateDamage(
                        target,
                        DamageType.Physical,
                        Math.Max(target.MaxHealth * 0.1, 100));
                }
                if (Tiamat.IsReady)
                {
                    dmgTotal += Player.CalculateDamage(target, DamageType.Physical, Player.TotalAttackDamage);
                }
                if (Hydra.IsReady)
                {
                    dmgTotal += Player.CalculateDamage(target, DamageType.Physical, Player.TotalAttackDamage);
                }
            }
            if (useQ)
            {
                dmgTotal += Q.GetDamage(target);
                manaTotal += Q.Instance.ManaCost;
            }
            if (useW)
            {
                if (useQ)
                {
                    dmgTotal += Q.GetDamage(target) / 2;
                }
                if (WState == 0)
                {
                    manaTotal += W.Instance.ManaCost;
                }
            }
            if (useE)
            {
                dmgTotal += E.GetDamage(target);
                manaTotal += E.Instance.ManaCost;
            }
            dmgTotal += Player.GetAutoAttackDamage(target) * 2;
            if (HaveR(target))
            {
                dmgTotal += Player.CalculateDamage(
                    target,
                    DamageType.Physical,
                    new[] { 0.25, 0.35, 0.45 }[R.Level - 1] * dmgTotal + Player.TotalAttackDamage);
            }
            return new List<double> { dmgTotal, manaTotal };
        }

        private static bool HaveR(Obj_AI_Hero target)
        {
            return target.HasBuff("zedrtargetmark");
        }

        private static void Hybrid()
        {
            var target = GetTarget;
            if (target == null)
            {
                return;
            }
            var mode = MainMenu["Hybrid"]["Mode"].GetValue<MenuList>().Index;
            if (mode == 0 && WState == 0)
            {
                CastW(target, CanW(target));
            }
            if (mode < 2)
            {
                CastE();
            }
            CastQ(target);
        }

        private static bool IsInRangeE(Obj_AI_Hero target)
        {
            var pos = E.VPredictionPos(target);
            return pos.DistanceToPlayer() < E.Range || (wShadow.IsValid() && wShadow.Distance(pos) < E.Range)
                   || (rShadow.IsValid() && rShadow.Distance(pos) < E.Range)
                   || (IsCastingW && wMissile.EndPosition.Distance(pos) < E.Range);
        }

        private static bool IsKillByMark(Obj_AI_Hero target)
        {
            return HaveR(target) && deathMark != null && target.Distance(deathMark) < 150;
        }

        private static void KillSteal()
        {
            if (MainMenu["KillSteal"]["Q"] && Q.IsReady())
            {
                var targets =
                    Variables.TargetSelector.GetTargets(Q.Range + Q.Width / 2 + RangeTarget, Q.DamageType)
                        .Where(i => !IsKillByMark(i) && i.Health + i.PhysicalShield <= Q.GetDamage(i))
                        .ToList();
                if (targets.Count > 0)
                {
                    foreach (var target in targets)
                    {
                        spellQ.UpdateSourcePosition();
                        if (CastQKill(spellQ, target))
                        {
                            break;
                        }
                        if (WShadowCanQ)
                        {
                            spellQ.UpdateSourcePosition(wShadow.ServerPosition, wShadow.ServerPosition);
                            if (CastQKill(spellQ, target, false))
                            {
                                break;
                            }
                        }
                        else if (IsCastingW)
                        {
                            spellQ.UpdateSourcePosition(wMissile.EndPosition, wMissile.EndPosition);
                            if (CastQKill(spellQ, target, false))
                            {
                                break;
                            }
                        }
                        if (RShadowCanQ)
                        {
                            spellQ.UpdateSourcePosition(rShadow.ServerPosition, rShadow.ServerPosition);
                            CastQKill(spellQ, target, false);
                        }
                    }
                }
            }
            if (MainMenu["KillSteal"]["E"] && E.IsReady())
            {
                CastE(true);
            }
        }

        private static void LastHit()
        {
            if (!MainMenu["LastHit"]["Q"] || !Q.IsReady() || Player.Spellbook.IsAutoAttacking)
            {
                return;
            }
            var minions =
                GameObjects.EnemyMinions.Where(
                    i =>
                    (i.IsMinion() || i.IsPet(false)) && i.IsValidTarget(Q.Range) && Q.GetHealthPrediction(i) > 0
                    && Q.GetHealthPrediction(i) <= Q.GetDamage(i)
                    && (i.IsUnderAllyTurret() || (i.IsUnderEnemyTurret() && !Player.IsUnderEnemyTurret())
                        || i.DistanceToPlayer() > i.GetRealAutoAttackRange() + 50
                        || i.Health > Player.GetAutoAttackDamage(i))).OrderByDescending(i => i.MaxHealth).ToList();
            if (minions.Count == 0)
            {
                return;
            }
            minions.ForEach(i => CastQKill(Q, i));
        }

        private static void OnDraw(EventArgs args)
        {
            if (Player.IsDead)
            {
                return;
            }
            if (MainMenu["Draw"]["Q"] && Q.Level > 0)
            {
                Drawing.DrawCircle(Player.Position, Q.Range, Q.IsReady() ? Color.LimeGreen : Color.IndianRed);
            }
            if (MainMenu["Draw"]["W"] && W.Level > 0)
            {
                Drawing.DrawCircle(Player.Position, W.Range, W.IsReady() ? Color.LimeGreen : Color.IndianRed);
            }
            if (MainMenu["Draw"]["E"] && E.Level > 0)
            {
                Drawing.DrawCircle(Player.Position, E.Range, E.IsReady() ? Color.LimeGreen : Color.IndianRed);
            }
            if (R.Level > 0)
            {
                var useR = MainMenu["Combo"]["R"].GetValue<MenuKeyBind>().Active;
                if (RState == 0)
                {
                    if (MainMenu["Draw"]["R"])
                    {
                        Drawing.DrawCircle(Player.Position, R.Range, Color.LimeGreen);
                    }
                    if (MainMenu["Draw"]["RStop"] && useR)
                    {
                        Drawing.DrawCircle(Player.Position, MainMenu["Combo"]["RStopRange"], Color.Orange);
                    }
                }
                if (MainMenu["Draw"]["UseR"])
                {
                    var pos = Drawing.WorldToScreen(Player.Position);
                    var text =
                        $"Use R In Combo: {(useR ? "On" : "Off")} [{MainMenu["Combo"]["R"].GetValue<MenuKeyBind>().Key}]";
                    Drawing.DrawText(
                        pos.X - (float)Drawing.GetTextExtent(text).Width / 2,
                        pos.Y + 20,
                        useR ? Color.White : Color.Gray,
                        text);
                }
            }
            if (MainMenu["Draw"]["Target"])
            {
                var target = GetTarget;
                if (target != null)
                {
                    Drawing.DrawCircle(target.Position, target.BoundingRadius * 1.5f, Color.Aqua);
                }
            }
            if (MainMenu["Draw"]["DMark"] && rShadow.IsValid())
            {
                var target = GameObjects.EnemyHeroes.FirstOrDefault(i => i.IsValidTarget() && IsKillByMark(i));
                if (target != null)
                {
                    var pos = Drawing.WorldToScreen(Player.Position);
                    var text = "Death Mark: " + target.ChampionName;
                    Drawing.DrawText(pos.X - (float)Drawing.GetTextExtent(text).Width / 2, pos.Y + 40, Color.Red, text);
                }
            }
            if (MainMenu["Draw"]["WPos"] && wShadow.IsValid())
            {
                Drawing.DrawCircle(wShadow.Position, wShadow.BoundingRadius, Color.MediumSlateBlue);
                var pos = Drawing.WorldToScreen(wShadow.Position);
                Drawing.DrawText(pos.X - (float)Drawing.GetTextExtent("W").Width / 2, pos.Y, Color.BlueViolet, "W");
            }
            if (MainMenu["Draw"]["RPos"] && rShadow.IsValid())
            {
                Drawing.DrawCircle(rShadow.Position, rShadow.BoundingRadius, Color.MediumSlateBlue);
                var pos = Drawing.WorldToScreen(rShadow.Position);
                Drawing.DrawText(pos.X - (float)Drawing.GetTextExtent("R").Width / 2, pos.Y, Color.BlueViolet, "R");
            }
        }

        private static void OnUpdate(EventArgs args)
        {
            if (Player.IsDead || MenuGUI.IsChatOpen || MenuGUI.IsShopOpen || Player.IsRecalling())
            {
                return;
            }
            KillSteal();
            switch (Variables.Orbwalker.GetActiveMode())
            {
                case OrbwalkingMode.Combo:
                    Combo();
                    break;
                case OrbwalkingMode.Hybrid:
                    Hybrid();
                    break;
                case OrbwalkingMode.LastHit:
                    LastHit();
                    break;
                case OrbwalkingMode.None:
                    if (MainMenu["FleeW"].GetValue<MenuKeyBind>().Active)
                    {
                        Variables.Orbwalker.Move(Game.CursorPos);
                        if (WState == 0)
                        {
                            W.Cast(Game.CursorPos);
                        }
                        else if (WState == 1)
                        {
                            W.Cast();
                        }
                    }
                    break;
            }
            if (Variables.Orbwalker.GetActiveMode() != OrbwalkingMode.Combo)
            {
                if (MainMenu["Hybrid"]["AutoE"])
                {
                    CastE();
                }
                if (Variables.Orbwalker.GetActiveMode() != OrbwalkingMode.Hybrid)
                {
                    AutoQ();
                }
            }
        }

        private static void Swap(Obj_AI_Hero target)
        {
            var eCanKill = E.CanCast(target) && E.CanHitCircle(target)
                           && target.Health + target.PhysicalShield <= E.GetDamage(target);
            var markCanKill = IsKillByMark(target);
            if (MainMenu["Combo"]["SwapIfKill"] && (markCanKill || eCanKill))
            {
                SwapCountEnemy();
                return;
            }
            if (Player.HealthPercent < MainMenu["Combo"]["SwapIfHpU"])
            {
                if (markCanKill || !eCanKill || Player.HealthPercent < target.HealthPercent)
                {
                    SwapCountEnemy();
                }
            }
            else if (MainMenu["Combo"]["SwapGap"].GetValue<MenuList>().Index > 0 && !E.IsInRange(target) && !markCanKill)
            {
                var wDist = WState == 1 && wShadow.IsValid() ? wShadow.Distance(target) : float.MaxValue;
                var rDist = RState == 1 && rShadow.IsValid() ? rShadow.Distance(target) : float.MaxValue;
                var minDist = Math.Min(wDist, rDist);
                if (minDist.Equals(float.MaxValue) || target.DistanceToPlayer() <= minDist)
                {
                    return;
                }
                var swapByW = Math.Abs(minDist - wDist) < float.Epsilon;
                var swapByR = Math.Abs(minDist - rDist) < float.Epsilon;
                if (swapByW && minDist < R.Range && !R.IsInRange(target)
                    && MainMenu["Combo"]["R"].GetValue<MenuKeyBind>().Active
                    && MainMenu["Combo"]["RCast" + target.ChampionName] && RState == 0 && CanR && W.Cast())
                {
                    return;
                }
                switch (MainMenu["Combo"]["SwapGap"].GetValue<MenuList>().Index)
                {
                    case 1:
                        if (IsInRangeE(target) && target.HealthPercent < 15 && Player.HealthPercent > 30
                            && (Q.IsReady() || E.IsReady()))
                        {
                            if (swapByW)
                            {
                                W.Cast();
                            }
                            else if (swapByR)
                            {
                                R.Cast();
                            }
                            return;
                        }
                        var combo = GetCombo(
                            target,
                            Q.IsReady() && minDist < Q.Range,
                            false,
                            E.IsReady() && minDist < E.Range);
                        if (minDist > target.GetRealAutoAttackRange())
                        {
                            combo[0] -= Player.GetAutoAttackDamage(target);
                        }
                        if (minDist > target.GetRealAutoAttackRange() + 100)
                        {
                            combo[0] -= Player.GetAutoAttackDamage(target);
                        }
                        if (target.Health + target.PhysicalShield > combo[0] || Player.Mana < combo[1])
                        {
                            return;
                        }
                        if (swapByW)
                        {
                            W.Cast();
                        }
                        else if (swapByR)
                        {
                            R.Cast();
                        }
                        break;
                    case 2:
                        if (minDist > 500)
                        {
                            return;
                        }
                        if (swapByW)
                        {
                            W.Cast();
                        }
                        else if (swapByR)
                        {
                            R.Cast();
                        }
                        break;
                }
            }
        }

        private static void SwapCountEnemy()
        {
            var wCount = WState == 1 && wShadow.IsValid() ? wShadow.CountEnemyHeroesInRange(400) : int.MaxValue;
            var rCount = RState == 1 && rShadow.IsValid() ? rShadow.CountEnemyHeroesInRange(400) : int.MaxValue;
            var minCount = Math.Min(rCount, wCount);
            if (minCount == int.MaxValue || Player.CountEnemyHeroesInRange(400) <= minCount)
            {
                return;
            }
            if (minCount == wCount)
            {
                W.Cast();
            }
            else if (minCount == rCount)
            {
                R.Cast();
            }
        }

        private static void TryEvading(List<Skillshot> hitBy, Vector2 to)
        {
            var dangerLevel = hitBy.Select(i => i.DangerLevel).Concat(new[] { 0 }).Max();
            var zedR1 =
                EvadeSpellDatabase.Spells.FirstOrDefault(
                    i =>
                    i.Enable && dangerLevel >= i.DangerLevel && i.IsReady && i.Slot == SpellSlot.R
                    && i.CheckSpellName == "zedr");
            var target =
                zedR1?.GetEvadeTargets(false, true)
                    .OrderByDescending(i => new Priority().GetDefaultPriority((Obj_AI_Hero)i))
                    .ThenBy(i => i.CountEnemyHeroesInRange(400))
                    .FirstOrDefault();
            if (target != null)
            {
                Player.Spellbook.CastSpell(zedR1.Slot, target);
            }
        }

        private static void UseItem(Obj_AI_Hero target)
        {
            if (target != null && (HaveR(target) || target.HealthPercent < 40 || Player.HealthPercent < 50))
            {
                if (Bilgewater.IsReady)
                {
                    Bilgewater.Cast(target);
                }
                if (BotRuinedKing.IsReady)
                {
                    BotRuinedKing.Cast(target);
                }
            }
            if (Youmuu.IsReady && Player.CountEnemyHeroesInRange(R.Range + E.Range) > 0)
            {
                Youmuu.Cast();
            }
            if (Tiamat.IsReady && Player.CountEnemyHeroesInRange(Tiamat.Range) > 0)
            {
                Tiamat.Cast();
            }
            if (Hydra.IsReady && Player.CountEnemyHeroesInRange(Hydra.Range) > 0)
            {
                Hydra.Cast();
            }
            if (Titanic.IsReady && Player.CountEnemyHeroesInRange(Titanic.Range) > 0)
            {
                Titanic.Cast();
            }
        }

        #endregion

        private static class EvadeTarget
        {
            #region Static Fields

            private static readonly List<Targets> DetectedTargets = new List<Targets>();

            private static readonly List<SpellData> Spells = new List<SpellData>();

            #endregion

            #region Methods

            internal static void Init()
            {
                LoadSpellData();
                var evadeMenu = MainMenu.Add(new Menu("EvadeTarget", "Evade Target"));
                {
                    evadeMenu.Bool("R", "Use R1");
                    foreach (var hero in
                        GameObjects.EnemyHeroes.Where(
                            i =>
                            Spells.Any(
                                a =>
                                string.Equals(
                                    a.ChampionName,
                                    i.ChampionName,
                                    StringComparison.InvariantCultureIgnoreCase))))
                    {
                        evadeMenu.Add(new Menu(hero.ChampionName.ToLowerInvariant(), "-> " + hero.ChampionName));
                    }
                    foreach (var spell in
                        Spells.Where(
                            i =>
                            GameObjects.EnemyHeroes.Any(
                                a =>
                                string.Equals(
                                    a.ChampionName,
                                    i.ChampionName,
                                    StringComparison.InvariantCultureIgnoreCase))))
                    {
                        ((Menu)evadeMenu[spell.ChampionName.ToLowerInvariant()]).Bool(
                            spell.MissileName,
                            spell.MissileName + " (" + spell.Slot + ")",
                            false);
                    }
                }
                Game.OnUpdate += OnUpdateTarget;
                GameObject.OnCreate += ObjSpellMissileOnCreate;
                GameObject.OnDelete += ObjSpellMissileOnDelete;
            }

            private static void LoadSpellData()
            {
                Spells.Add(
                    new SpellData { ChampionName = "Anivia", SpellNames = new[] { "frostbite" }, Slot = SpellSlot.E });
                Spells.Add(
                    new SpellData
                        {
                            ChampionName = "Brand", SpellNames = new[] { "brandwildfire", "brandwildfiremissile" },
                            Slot = SpellSlot.R
                        });
                Spells.Add(
                    new SpellData
                        {
                            ChampionName = "Caitlyn", SpellNames = new[] { "caitlynaceintheholemissile" },
                            Slot = SpellSlot.R
                        });
                Spells.Add(
                    new SpellData
                        {
                            ChampionName = "Leblanc", SpellNames = new[] { "leblancchaosorb", "leblancchaosorbm" },
                            Slot = SpellSlot.Q
                        });
                Spells.Add(new SpellData { ChampionName = "Lulu", SpellNames = new[] { "luluw" }, Slot = SpellSlot.W });
                Spells.Add(
                    new SpellData { ChampionName = "Syndra", SpellNames = new[] { "syndrar" }, Slot = SpellSlot.R });
                Spells.Add(
                    new SpellData
                        { ChampionName = "TwistedFate", SpellNames = new[] { "bluecardattack" }, Slot = SpellSlot.W });
                Spells.Add(
                    new SpellData
                        { ChampionName = "TwistedFate", SpellNames = new[] { "goldcardattack" }, Slot = SpellSlot.W });
                Spells.Add(
                    new SpellData
                        { ChampionName = "TwistedFate", SpellNames = new[] { "redcardattack" }, Slot = SpellSlot.W });
                Spells.Add(
                    new SpellData
                        { ChampionName = "Vayne", SpellNames = new[] { "vaynecondemnmissile" }, Slot = SpellSlot.E });
                Spells.Add(
                    new SpellData
                        { ChampionName = "Veigar", SpellNames = new[] { "veigarprimordialburst" }, Slot = SpellSlot.R });
            }

            private static void ObjSpellMissileOnCreate(GameObject sender, EventArgs args)
            {
                var missile = sender as MissileClient;
                if (missile == null || !missile.IsValid)
                {
                    return;
                }
                var caster = missile.SpellCaster as Obj_AI_Hero;
                if (caster == null || !caster.IsValid || caster.Team == Player.Team || !missile.Target.IsMe)
                {
                    return;
                }
                var spellData =
                    Spells.FirstOrDefault(
                        i =>
                        i.SpellNames.Contains(missile.SData.Name.ToLower())
                        && MainMenu["EvadeTarget"][i.ChampionName.ToLowerInvariant()][i.MissileName]);
                if (spellData == null)
                {
                    return;
                }
                DetectedTargets.Add(new Targets { Obj = missile });
            }

            private static void ObjSpellMissileOnDelete(GameObject sender, EventArgs args)
            {
                var missile = sender as MissileClient;
                if (missile == null || !missile.IsValid)
                {
                    return;
                }
                var caster = missile.SpellCaster as Obj_AI_Hero;
                if (caster == null || !caster.IsValid || caster.Team == Player.Team)
                {
                    return;
                }
                DetectedTargets.RemoveAll(i => i.Obj.NetworkId == missile.NetworkId);
            }

            private static void OnUpdateTarget(EventArgs args)
            {
                if (Player.IsDead)
                {
                    return;
                }
                if (Player.HasBuffOfType(BuffType.SpellShield) || Player.HasBuffOfType(BuffType.SpellImmunity))
                {
                    return;
                }
                if (!MainMenu["EvadeTarget"]["R"] || RState != 0)
                {
                    return;
                }
                if (DetectedTargets.Any(i => Player.Distance(i.Obj) < 500))
                {
                    var target = R.GetTarget();
                    if (target != null)
                    {
                        R.CastOnUnit(target);
                    }
                }
            }

            #endregion

            private class SpellData
            {
                #region Fields

                public string ChampionName;

                public SpellSlot Slot;

                public string[] SpellNames = { };

                #endregion

                #region Public Properties

                public string MissileName => this.SpellNames.First();

                #endregion
            }

            private class Targets
            {
                #region Fields

                public MissileClient Obj;

                #endregion
            }
        }
    }
}