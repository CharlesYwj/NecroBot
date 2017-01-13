#region using directives

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PoGo.NecroBot.Logic.Event;
using PoGo.NecroBot.Logic.PoGoUtils;
using PoGo.NecroBot.Logic.State;
using POGOProtos.Inventory.Item;
using POGOProtos.Map.Fort;
using PoGo.NecroBot.Logic.Logging;
using POGOProtos.Networking.Responses;
using POGOProtos.Enums;
using PoGo.NecroBot.Logic.Event.Gym;
using PokemonGo.RocketAPI.Extensions;
using POGOProtos.Data;
using POGOProtos.Data.Battle;
using PokemonGo.RocketAPI.Exceptions;

#endregion

namespace PoGo.NecroBot.Logic.Tasks
{
    public class UseGymBattleTask
    {
        public static async Task Execute(ISession session, CancellationToken cancellationToken, FortData gym, FortDetailsResponse fortInfo)
        {
            if (!session.LogicSettings.GymConfig.Enable || gym.Type != FortType.Gym) return;

            cancellationToken.ThrowIfCancellationRequested();
            var distance = session.Navigation.WalkStrategy.CalculateDistance(session.Client.CurrentLatitude, session.Client.CurrentLongitude, gym.Latitude, gym.Longitude);
            if (fortInfo != null)
            {
                session.EventDispatcher.Send(new GymWalkToTargetEvent()
                {
                    Name = fortInfo.Name,
                    Distance = distance,
                    Latitude = fortInfo.Latitude,
                    Longitude = fortInfo.Longitude
                });

                var fortDetails = await session.Client.Fort.GetGymDetails(gym.Id, gym.Latitude, gym.Longitude);

                    if (fortDetails.Result == GetGymDetailsResponse.Types.Result.Success)
                    {
                        var player = session.Profile.PlayerData;
                        await EnsureJoinTeam(session, player);

                        //Do gym tutorial - tobe coded

                        session.EventDispatcher.Send(new GymDetailInfoEvent()
                        {
                            Team = fortDetails.GymState.FortData.OwnedByTeam,
                            Point = gym.GymPoints,
                            Name = fortDetails.Name,
                        });

                        if (player.Team != TeamColor.Neutral && (fortDetails.GymState.FortData.OwnedByTeam == player.Team || fortDetails.GymState.FortData.OwnedByTeam == TeamColor.Neutral))
                        {
                            //trainning logic will come here
                            await DeployPokemonToGym(session, fortInfo, fortDetails);
                        }
                        else
                        {
                            await StartGymAttackLogic(session, fortInfo, fortDetails, gym, cancellationToken);
                            //Logger.Write($"No action... This gym is defending by other color", LogLevel.Gym, ConsoleColor.White);
                        }
                    }
                    else
                    {
                        Logger.Write($"You are not level 5 yet, come back later...", LogLevel.Gym, ConsoleColor.White);
                    }
            }
            else
            {
                // ReSharper disable once PossibleNullReferenceException
                Logger.Write($"Ignoring  Gym : {fortInfo.Name} - ", LogLevel.Gym, ConsoleColor.Cyan);
            }
        }

        private static async Task StartGymAttackLogic(ISession session, FortDetailsResponse fortInfo,
            GetGymDetailsResponse fortDetails, FortData gym, CancellationToken cancellationToken)
        {
            if (session.LogicSettings.GymConfig.MaxGymLevelToAttack < GetGymLevel(gym.GymPoints))
            {
                Logger.Write($"This is gym level {GetGymLevel(gym.GymPoints)} > {session.LogicSettings.GymConfig.MaxGymLevelToAttack} in your config. Bot walk away...", LogLevel.Gym, ConsoleColor.Red);
                return;
            }
            var defenders = fortDetails.GymState.Memberships.Select(x => x.PokemonData).ToList();

            if (session.LogicSettings.GymConfig.MaxDefendersToAttack < defenders.Count)
            {
                Logger.Write($"This is gym has   {defenders.Count} defender  > {session.LogicSettings.GymConfig.MaxDefendersToAttack} in your config. Bot walk away...", LogLevel.Gym, ConsoleColor.Red);
                return;
            }

            await session.Inventory.RefreshCachedInventory();
            var badassPokemon = await session.Inventory.GetHighestCpForGym(6);
            var pokemonDatas = badassPokemon as PokemonData[] ?? badassPokemon.ToArray();
            if (defenders.Count == 0) return;

            Logger.Write("Start battle with : " + string.Join(", ", defenders.Select(x => x.PokemonId.ToString())));

            // Heal pokemon
            foreach (var pokemon in pokemonDatas)
            {
                if (pokemon.Stamina <= 0)
                    await RevivePokemon(session, pokemon);
                if (pokemon.Stamina < pokemon.StaminaMax)
                    await HealPokemon(session, pokemon);
            }
            await Task.Delay(2000);

            var index = 0;
            bool isVictory = true;
            List<BattleAction> battleActions = new List<BattleAction>();
            ulong defenderPokemonId = defenders.First().Id;

            while (index < defenders.Count())
            {
                var thisAttackActions = new List<BattleAction>();

                StartGymBattleResponse result = null;
                int retries = 3;
                do
                {
                    try
                    {
                        await Task.Delay(500);
                        result = await StartBattle(session, gym, pokemonDatas, defenders.FirstOrDefault(x=>x.Id == defenderPokemonId));
                    }
                    catch (APIBadRequestException e)
                    {
                        Logger.Write("SHIT in start battle", LogLevel.Warning);
                        Logger.Write(e.Message, LogLevel.Error, ConsoleColor.Magenta);
                        Logger.Write(e.StackTrace, LogLevel.Error, ConsoleColor.Magenta);
                        await Task.Delay(500);
                    }
                }
                while ((result== null ||  result.Result != StartGymBattleResponse.Types.Result.Success) && --retries >0);

                index++;
                // If we can't start battle in 10 tries, let's skip the gym
                if ( result == null || result.Result == StartGymBattleResponse.Types.Result.Unset)
                {
                    session.EventDispatcher.Send(new GymErrorUnset { GymName = fortInfo.Name });
                    isVictory = false;
                    break;
                }

                if (result.Result != StartGymBattleResponse.Types.Result.Success) break;
                switch (result.BattleLog.State)
                {
                    case BattleState.Active:
                        Logger.Write($"Time to start Attack Mode", LogLevel.Gym, ConsoleColor.DarkYellow);
                        thisAttackActions = await AttackGym(session, cancellationToken, gym, result);
                        battleActions.AddRange(thisAttackActions);
                        break;
                    case BattleState.Defeated:
                        isVictory = false;
                        break;
                    case BattleState.StateUnset:
                        isVictory = false;
                        break;
                    case BattleState.TimedOut:
                        isVictory = false;
                        break;
                    case BattleState.Victory:
                        break;
                    default:
                        Logger.Write($"Unhandled result starting gym battle:\n{result}");
                        break;
                }
                //check results .....
                var rewarded = battleActions.Select(x => x.BattleResults?.PlayerExperienceAwarded).Where(x => x != null);
                //if(rewarded!= null && rewarded.Count()>0)
                //{
                //    Logger.Write("Seem we win battle");
                //    fighting = false;
                //}

                var lastAction = battleActions.LastOrDefault();

                if (lastAction.Type == BattleActionType.ActionTimedOut ||
                    lastAction.Type == BattleActionType.ActionUnset ||
                    lastAction.Type == BattleActionType.ActionDefeat)
                {
                    isVictory = false;
                    break;
                }

                var faintedPKM = battleActions.Where(x => x != null && x.Type == BattleActionType.ActionFaint).Select(x => x.ActivePokemonId).Distinct();
                //Logger.Write("Defeted pokemons ids: " + string.Join(", ", faintedPKM), LogLevel.Gym, ConsoleColor.Magenta);

                //Logger.Write("Attackers before: " + string.Join(", ", pokemonDatas.Select(x=>x.Id)), LogLevel.Gym, ConsoleColor.Magenta);
                var livePokemons = pokemonDatas.Where(x => !faintedPKM.Any(y => y == x.Id));
                var faintedPokemons = pokemonDatas.Where(x => faintedPKM.Any(y => y == x.Id));
                pokemonDatas = livePokemons.Concat(faintedPokemons).ToArray();
                //Logger.Write("Attackers for new turn: " + string.Join(", ", pokemonDatas.Select(x => x.Id)), LogLevel.Gym, ConsoleColor.Magenta);

                if (lastAction.Type == BattleActionType.ActionVictory)
                {
                    if(lastAction.BattleResults!= null)
                    {
                        var exp = lastAction.BattleResults.PlayerExperienceAwarded;
                        var point = lastAction.BattleResults.GymPointsDelta;
                        defenderPokemonId = unchecked((ulong)lastAction.BattleResults.NextDefenderPokemonId);

                        //Logger.Write(string.Format("Exp: {0}, Gym points: {1}, Next defender id: {2}", exp, point, defenderPokemonId), LogLevel.Gym, ConsoleColor.Magenta);
                    }
                    continue;
                }
            }

            if (isVictory)
            {
                fortInfo = await session.Client.Fort.GetFort(gym.Id, gym.Latitude, gym.Longitude);
                await Execute(session, cancellationToken, gym, fortInfo);
            }
        }

        private static async Task DeployPokemonToGym(ISession session, FortDetailsResponse fortInfo, GetGymDetailsResponse fortDetails)
        {
            var maxCount = 0;
            var points = fortDetails.GymState.FortData.GymPoints;
            if (points < 1600) maxCount = 1;
            else if (points < 4000) maxCount = 2;
            else if (points < 8000) maxCount = 3;
            else if (points < 12000) maxCount = 4;
            else if (points < 16000) maxCount = 5;
            else if (points < 20000) maxCount = 6;
            else if (points < 30000) maxCount = 7;
            else if (points < 40000) maxCount = 8;
            else if (points < 50000) maxCount = 9;
            else maxCount = 10;

            var availableSlots = maxCount - fortDetails.GymState.Memberships.Count();

            if (availableSlots > 0)
            {
                var deployed = await session.Inventory.GetDeployedPokemons();
                if (!deployed.Any(a => a.DeployedFortId == fortInfo.FortId))
                {
                    var pokemon = await GetDeployablePokemon(session);
                    if (pokemon != null)
                    {
                        var response = await session.Client.Fort.FortDeployPokemon(fortInfo.FortId, pokemon.Id);
                        if (response.Result == FortDeployPokemonResponse.Types.Result.Success)
                        {
                            session.EventDispatcher.Send(new GymDeployEvent()
                            {
                                PokemonId = pokemon.PokemonId,
                                Name = fortDetails.Name
                            });
                            if (session.LogicSettings.GymConfig.CollectCoinAfterDeployed > 0)
                            {
                                var count = deployed.Count() + 1;
                                if (count >= session.LogicSettings.GymConfig.CollectCoinAfterDeployed)
                                {
                                    try
                                    {
                                        if (session.Profile.PlayerData.DailyBonus.NextCollectedTimestampMs <= DateTime.Now.ToUnixTime())
                                        {
                                            var collectDailyBonusResponse = await session.Client.Player.CollectDailyBonus();
                                            if (collectDailyBonusResponse.Result == CollectDailyBonusResponse.Types.Result.Success)
                                            {
                                                Logger.Write($"Collected {count * 10} coins", LogLevel.Gym, ConsoleColor.DarkYellow);
                                            }
                                        }
                                        else
                                            Logger.Write($"You need to wait {session.Profile.PlayerData.DailyBonus.NextCollectedTimestampMs - DateTime.Now.ToUnixTime()}ms for daily bonus", LogLevel.Info, ConsoleColor.Magenta);
                                    }
                                    catch (APIBadRequestException e)
                                    {
                                        Logger.Write("SHIT in getting coins", LogLevel.Warning);
                                        Logger.Write(e.Message, LogLevel.Error, ConsoleColor.Magenta);
                                        Logger.Write(e.StackTrace, LogLevel.Error, ConsoleColor.Magenta);
                                        await Task.Delay(500);
                                    }
                                }
                            }
                        }
                        else
                            Logger.Write(string.Format("Deploy pokemon failed with result: {0}", response.Result), LogLevel.Gym, ConsoleColor.Magenta);
                    }
                    else
                        Logger.Write($"You already have pokemon deployed here", LogLevel.Gym);
                }
            }
            else
            {
                string message = string.Format("No action. No FREE slots in GYM {0}/{1}", fortDetails.GymState.Memberships.Count(), maxCount);
                Logger.Write(message, LogLevel.Gym, ConsoleColor.White);
            }
        }

        public static async Task RevivePokemon(ISession session, PokemonData pokemon)
        {
            var normalPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemPotion);
            var superPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemSuperPotion);
            var hyperPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemHyperPotion);

            var healPower = normalPotions * 20 + superPotions * 50 + hyperPotions * 200;

            var normalRevives = await session.Inventory.GetItemAmountByType(ItemId.ItemRevive);

			if(healPower >= pokemon.StaminaMax/2 && normalRevives>0 && pokemon.Stamina <= 0)
            {
                var ret = await session.Client.Inventory.UseItemRevive(ItemId.ItemRevive, pokemon.Id);
                switch (ret.Result)
                {
                    case UseItemReviveResponse.Types.Result.Success:
                        pokemon.Stamina = ret.Stamina;
                        session.EventDispatcher.Send(new EventUsedRevive
                        {
                            Type = "normal",
                            PokemonCp = pokemon.Cp,
                            PokemonId = pokemon.PokemonId.ToString(),
                            Remaining = (normalRevives - 1)
                        });
                        break;
                    case UseItemReviveResponse.Types.Result.ErrorDeployedToFort:
                        Logger.Write(
                            $"Pokemon: {pokemon.PokemonId} (CP: {pokemon.Cp}) is already deployed to a gym...");
                        return;
                    case UseItemReviveResponse.Types.Result.ErrorCannotUse:
                        return;
                    default:
                        return;
                }
                return;
            }
			
            var maxRevives = await session.Inventory.GetItemAmountByType(ItemId.ItemMaxRevive);
            if (maxRevives > 0 && pokemon.Stamina <= 0)
            {
                var ret = await session.Client.Inventory.UseItemRevive(ItemId.ItemMaxRevive, pokemon.Id);
                switch (ret.Result)
                {
                    case UseItemReviveResponse.Types.Result.Success:
                        pokemon.Stamina = ret.Stamina;
                        session.EventDispatcher.Send(new EventUsedRevive
                        {
                            Type = "max",
                            PokemonCp = pokemon.Cp,
                            PokemonId = pokemon.PokemonId.ToString(),
                            Remaining = (maxRevives - 1)
                        });
                        break;

                    case UseItemReviveResponse.Types.Result.ErrorDeployedToFort:
                        Logger.Write($"Pokemon: {pokemon.PokemonId} (CP: {pokemon.Cp}) is already deployed to a gym...");
                        return;

                    case UseItemReviveResponse.Types.Result.ErrorCannotUse:
                        return;

                    default:
                        return;
                }
            }
        }

        private static async Task<bool> UsePotion(ISession session, PokemonData pokemon, int normalPotions)
        {
            var ret = await session.Client.Inventory.UseItemPotion(ItemId.ItemPotion, pokemon.Id);
            switch (ret.Result)
            {
                case UseItemPotionResponse.Types.Result.Success:
                    pokemon.Stamina = ret.Stamina;
                    session.EventDispatcher.Send(new EventUsedPotion
                    {
                        Type = "normal",
                        PokemonCp = pokemon.Cp,
                        PokemonId = pokemon.PokemonId.ToString(),
                        Remaining = (normalPotions - 1)
                    });
                    break;

                case UseItemPotionResponse.Types.Result.ErrorDeployedToFort:
                    Logger.Write($"Pokemon: {pokemon.PokemonId} (CP: {pokemon.Cp}) is already deployed to a gym...");
                    return false;

                case UseItemPotionResponse.Types.Result.ErrorCannotUse:
                    return false;

                default:
                    return false;
            }
            return true;
        }

        private static async Task<bool> UseSuperPotion(ISession session, PokemonData pokemon, int superPotions)
        {
            var ret = await session.Client.Inventory.UseItemPotion(ItemId.ItemSuperPotion, pokemon.Id);
            switch (ret.Result)
            {
                case UseItemPotionResponse.Types.Result.Success:
                    pokemon.Stamina = ret.Stamina;
                    session.EventDispatcher.Send(new EventUsedPotion
                    {
                        Type = "super",
                        PokemonCp = pokemon.Cp,

                        PokemonId = pokemon.PokemonId.ToString(),
                        Remaining = (superPotions - 1)
                    });
                    break;

                case UseItemPotionResponse.Types.Result.ErrorDeployedToFort:
                    Logger.Write($"Pokemon: {pokemon.PokemonId} (CP: {pokemon.Cp}) is already deployed to a gym...");
                    return false;

                case UseItemPotionResponse.Types.Result.ErrorCannotUse:
                    return false;

                default:
                    return false;
            }
            return true;
        }

        private static async Task<bool> UseHyperPotion(ISession session, PokemonData pokemon, int hyperPotions)
        {
            var ret = await session.Client.Inventory.UseItemPotion(ItemId.ItemHyperPotion, pokemon.Id);
            switch (ret.Result)
            {
                case UseItemPotionResponse.Types.Result.Success:
                    pokemon.Stamina = ret.Stamina;
                    session.EventDispatcher.Send(new EventUsedPotion
                    {
                        Type = "hyper",
                        PokemonCp = pokemon.Cp,
                        PokemonId = pokemon.PokemonId.ToString(),
                        Remaining = (hyperPotions - 1)
                    });
                    break;

                case UseItemPotionResponse.Types.Result.ErrorDeployedToFort:
                    Logger.Write($"Pokemon: {pokemon.PokemonId} (CP: {pokemon.Cp}) is already deployed to a gym...");
                    return false;

                case UseItemPotionResponse.Types.Result.ErrorCannotUse:
                    return false;

                default:
                    return false;
            }
            return true;
        }

        private static async Task<bool> UseMaxPotion(ISession session, PokemonData pokemon, int maxPotions)
        {
            var ret = await session.Client.Inventory.UseItemPotion(ItemId.ItemMaxPotion, pokemon.Id);
            switch (ret.Result)
            {
                case UseItemPotionResponse.Types.Result.Success:
                    pokemon.Stamina = ret.Stamina;
                    session.EventDispatcher.Send(new EventUsedPotion
                    {
                        Type = "max",
                        PokemonCp = pokemon.Cp,
                        PokemonId = pokemon.PokemonId.ToString(),
                        Remaining = maxPotions
                    });
                    break;

                case UseItemPotionResponse.Types.Result.ErrorDeployedToFort:
                    Logger.Write($"Pokemon: {pokemon.PokemonId} (CP: {pokemon.Cp}) is already deployed to a gym...");
                    return false;

                case UseItemPotionResponse.Types.Result.ErrorCannotUse:
                    return false;

                default:
                    return false;
            }
            return true;
        }

        public static async Task<bool> HealPokemon(ISession session, PokemonData pokemon)
        {
            var normalPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemPotion);
            var superPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemSuperPotion);
            var hyperPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemHyperPotion);
            var maxPotions = await session.Inventory.GetItemAmountByType(ItemId.ItemMaxPotion);

            var healPower = normalPotions * 20 + superPotions * 50 + hyperPotions * 200;

            if (healPower < (pokemon.StaminaMax - pokemon.Stamina) && maxPotions > 0)
            {
                await session.Inventory.UpdateInventoryItem(ItemId.ItemMaxPotion, -1);
                return await UseMaxPotion(session, pokemon, maxPotions);
            }

            while (normalPotions + superPotions + hyperPotions > 0 && (pokemon.Stamina < pokemon.StaminaMax))
            {
                if (((pokemon.StaminaMax - pokemon.Stamina) > 200 || ((normalPotions * 20 + superPotions * 50) < (pokemon.StaminaMax - pokemon.Stamina))) && hyperPotions > 0)
                {
                    if (!await UseHyperPotion(session, pokemon, hyperPotions))
                        return false;
                    hyperPotions--;
                    await session.Inventory.UpdateInventoryItem(ItemId.ItemHyperPotion, -1);
                }
                else
                if (((pokemon.StaminaMax - pokemon.Stamina) > 50 || normalPotions * 20 < (pokemon.StaminaMax - pokemon.Stamina)) && superPotions > 0)
                {
                    if (!await UseSuperPotion(session, pokemon, superPotions))
                        return false;
                    superPotions--;
                    await session.Inventory.UpdateInventoryItem(ItemId.ItemSuperPotion, -1);
                }
                else
                {
                    if (!await UsePotion(session, pokemon, normalPotions))
                        return false;
                    normalPotions--;
                    await session.Inventory.UpdateInventoryItem(ItemId.ItemPotion, -1);
                }
            }

            return pokemon.Stamina == pokemon.StaminaMax;
        }

        private static int _currentAttackerEnergy;

        // ReSharper disable once UnusedParameter.Local
        private static async Task<List<BattleAction>> AttackGym(ISession session, CancellationToken cancellationToken, FortData currentFortData, StartGymBattleResponse startResponse)
        {
            long serverMs = startResponse.BattleLog.BattleStartTimestampMs;
            var lastActions = startResponse.BattleLog.BattleActions.ToList();

            Logger.Write($"Gym battle started; fighting trainer: {startResponse.Defender.TrainerPublicProfile.Name}", LogLevel.Gym, ConsoleColor.Green);
            Logger.Write($"We are attacking: {startResponse.Defender.ActivePokemon.PokemonData.PokemonId}", LogLevel.Gym, ConsoleColor.White);
            Console.WriteLine("\r\n");

            int loops = 0;
            List<BattleAction> emptyActions = new List<BattleAction>();
            BattleAction emptyAction = new BattleAction();
            PokemonData attacker = null;
            PokemonData defender = null;
            _currentAttackerEnergy = 0;

            while (true)
            {
                try
                {
                    var last = lastActions.LastOrDefault();

                    var attackActionz = await GetActions(session, serverMs, attacker, defender, _currentAttackerEnergy);

                    List<BattleAction> a1 = (last == null || last.Type == BattleActionType.ActionVictory || last.Type == BattleActionType.ActionDefeat ? emptyActions : attackActionz);
                    BattleAction a2 = (last == null || last.Type == BattleActionType.ActionVictory || last.Type == BattleActionType.ActionDefeat ? emptyAction : last);
                    //Logger.Write(string.Format("Sizeof of list of action passed: {0}, last one: {1}", a1.Count, a2.Type), LogLevel.Gym, ConsoleColor.Magenta);
                    var attackResult = await session.Client.Fort.AttackGym(currentFortData.Id, startResponse.BattleId, a1, a2);

                    loops++;

                    if (attackResult.Result == AttackGymResponse.Types.Result.Success)
                    {
                        defender = attackResult.ActiveDefender?.PokemonData;
                        if (attackResult.BattleLog != null && attackResult.BattleLog.BattleActions.Count > 0)
                            lastActions.AddRange(attackResult.BattleLog.BattleActions);
                        serverMs = attackResult.BattleLog.ServerMs;

                        switch (attackResult.BattleLog.State)
                        {
                            case BattleState.Active:
                                _currentAttackerEnergy = attackResult.ActiveAttacker.CurrentEnergy;
                                attacker = attackResult.ActiveAttacker.PokemonData;
                                Console.SetCursorPosition(0, Console.CursorTop - 1);
                                Logger.Write(
                                    $"(GYM ATTACK) : Defender {attackResult.ActiveDefender.PokemonData.PokemonId.ToString()  } HP {attackResult.ActiveDefender.CurrentHealth} - Attacker  {attackResult.ActiveAttacker.PokemonData.PokemonId.ToString()}   HP/Sta {attackResult.ActiveAttacker.CurrentHealth}/{attackResult.ActiveAttacker.CurrentEnergy}        ");

                                break;

                            case BattleState.Defeated:
                                Logger.Write(
                                    $"We were defeated... (AttackGym)");
                                return lastActions;
                            case BattleState.TimedOut:
                                Logger.Write(
                                    $"Our attack timed out...:");
                                return lastActions;
                            case BattleState.StateUnset:
                                Logger.Write(
                                    $"State was unset?: {attackResult}");
                                return lastActions;

                            case BattleState.Victory:
                                Logger.Write(
                                    $"We were victorious!: ");
                                return lastActions;
                            default:
                                Logger.Write(
                                    $"Unhandled attack response: {attackResult}");
                                continue;
                        }
                        Debug.Write($"{attackResult}");


                        await Task.Delay(attackActionz.Sum(x => x.DurationMs) + 10);

                       // Logger.Write($"Finished sleeping, starting another attack");

                    }
                    else
                    {
                        Logger.Write($"Unexpected attack result:\n{attackResult}");
                        continue;
                    }


                }

                catch (APIBadRequestException e)
                {
                    Logger.Write("Shit!!!! Bad request send to server -", LogLevel.Warning);
                    Logger.Write(e.Message, LogLevel.Error, ConsoleColor.Magenta);
                    Logger.Write(e.StackTrace, LogLevel.Error, ConsoleColor.Magenta);
                };
            }
            return lastActions;

        }

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static DateTime DateTimeFromUnixTimestampMillis(long millis)
        {
            return UnixEpoch.AddMilliseconds(millis);
        }

        //private static int _pos;
        public static async Task<List<BattleAction>> GetActions(ISession sessison, long serverMs, PokemonData attacker, PokemonData defender, int energy)
        {
            Random rnd = new Random();
            List<BattleAction> actions = new List<BattleAction>();
            DateTime now = DateTimeFromUnixTimestampMillis(serverMs);
            //Logger.Write($"AttackGym Count: {_pos}");

            var inventory = sessison.Inventory;

            if (attacker != null && defender != null)
            {
                //var move1 = PokemonMoveMetaRegistry.GetMeta(attacker.Move1);
                //var move2 = PokemonMoveMetaRegistry.GetMeta(attacker.Move2);
                //  Logger.Write($"Retrieved Move Metadata, Move1: {move1.GetTime()} - Move2: {move2.GetTime()}");

                var moveSetting = await inventory.GetMoveSetting(attacker.Move1);
                var specialMove = await inventory.GetMoveSetting(attacker.Move2);
                
                BattleAction action2 = new BattleAction();
                //if (Math.Abs(specialMove.EnergyDelta) <= energy)
                //{
                //    now = now.AddMilliseconds(specialMove.DurationMs);
                //    action2.Type = BattleActionType.ActionSpecialAttack;
                //    action2.DurationMs = specialMove.DurationMs;

                //    action2.DamageWindowsStartTimestampMs = specialMove.DamageWindowStartMs;
                //    action2.DamageWindowsEndTimestampMs = specialMove.DamageWindowEndMs;
                //}
                //else
                //{
                    now = now.AddMilliseconds(moveSetting.DurationMs);
                    action2.Type = BattleActionType.ActionAttack;
                    action2.DurationMs = moveSetting.DurationMs;

                    action2.DamageWindowsStartTimestampMs = moveSetting.DamageWindowStartMs;
                    action2.DamageWindowsEndTimestampMs = moveSetting.DamageWindowEndMs;
                //}
                action2.ActionStartMs = now.ToUnixTime();
                action2.TargetIndex = -1;
                action2.ActivePokemonId = attacker.Id;
                action2.TargetPokemonId = defender.Id;
                
                actions.Add(action2);

                return actions;
            }
            BattleAction action1 = new BattleAction();
            now = now.AddMilliseconds(500);
            action1.Type = BattleActionType.ActionAttack;
            action1.DurationMs = 500;
            action1.ActionStartMs = now.ToUnixTime();
            action1.TargetIndex = -1;
            if (defender != null)
                action1.ActivePokemonId = attacker.Id;

            actions.Add(action1);

            return actions;

        }

        private static async Task<StartGymBattleResponse> StartBattle(ISession session, FortData currentFortData, IEnumerable<PokemonData> attackers, PokemonData defender)
        {

            IEnumerable<PokemonData> currentPokemons = attackers;
            var gymInfo = await session.Client.Fort.GetGymDetails(currentFortData.Id, currentFortData.Latitude, currentFortData.Longitude);
            if (gymInfo.Result != GetGymDetailsResponse.Types.Result.Success)
            {
                return null;
            }

            var pokemonDatas = currentPokemons as PokemonData[] ?? currentPokemons.ToArray();
            //var defendingPokemon = gymInfo.GymState.Memberships.First().PokemonData.Id;
            var attackerPokemons = pokemonDatas.Select(pokemon => pokemon.Id);
            var attackingPokemonIds = attackerPokemons as ulong[] ?? attackerPokemons.ToArray();

            Logger.Write(
                $"Attacking Gym: {gymInfo.Name}, DefendingPokemons: { string.Join(", ", gymInfo.GymState.Memberships.Select(p => p.PokemonData.PokemonId).ToList()) }, Attacking: { gymInfo.GymState.Memberships.First().PokemonData.PokemonId } ({defender.Id}), We attack with: { string.Join(", ", attackingPokemonIds) }"
                , LogLevel.Gym, ConsoleColor.Magenta
                );
            var result = await session.Client.Fort.StartGymBattle(currentFortData.Id, defender.Id, attackingPokemonIds);

            if (result.Result == StartGymBattleResponse.Types.Result.Success)
            {
                switch (result.BattleLog.State)
                {
                    case BattleState.Active:
                        Logger.Write("Start new battle...");
                        //session.EventDispatcher.Send(new GymBattleStarted { GymName = gymInfo.Name });
                        return result;
                    case BattleState.Defeated:
                        Logger.Write($"We were defeated in battle.");
                        return result;
                    case BattleState.Victory:
                        Logger.Write($"We were victorious");
                        //_pos = 0;
                        return result;
                    case BattleState.StateUnset:
                        Logger.Write($"Error occoured: {result.BattleLog.State}");
                        break;
                    case BattleState.TimedOut:
                        Logger.Write($"Error occoured: {result.BattleLog.State}");
                        break;
                    default:
                        Logger.Write($"Unhandled occoured: {result.BattleLog.State}");
                        break;
                }
            }
            else if (result.Result == StartGymBattleResponse.Types.Result.ErrorGymBattleLockout)
            {
                return result;
            }
            else if (result.Result == StartGymBattleResponse.Types.Result.ErrorAllPokemonFainted)
            {
                return result;
            }
            else if (result.Result == StartGymBattleResponse.Types.Result.Unset)
            {
                return result;
            }
            return result;
        }

        private static async Task EnsureJoinTeam(ISession session, PlayerData player)
        {
            if (session.Profile.PlayerData.Team == TeamColor.Neutral)
            {
                var defaultTeam = (TeamColor)Enum.Parse(typeof(TeamColor), session.LogicSettings.GymConfig.DefaultTeam);
                var teamResponse = await session.Client.Player.SetPlayerTeam(defaultTeam);
                if (teamResponse.Status == SetPlayerTeamResponse.Types.Status.Success)
                {
                    player.Team = defaultTeam;
                }

                session.EventDispatcher.Send(new GymTeamJoinEvent()
                {
                    Team = defaultTeam,
                    Status = teamResponse.Status
                });
            }
        }

        private bool CanVisitGym()
        {
            return true;
        }

        private static int GetGymLevel(double points)
        {
            if (points < 2000) return 1;
            else
            if (points < 4000) return 2;
            else
                if (points < 6000) return 3;
            else if (points < 12000) return 4;
            else if (points < 16000) return 5;
            else if (points < 20000) return 6;
            else if (points < 30000) return 7;
            else if (points < 40000) return 8;
            else if (points < 50000) return 10;
            return 10;
        }

        private static async Task<PokemonData> GetDeployablePokemon(ISession session)
        {
            PokemonData pokemon = null;
            List<ulong> excluded = new List<ulong>();

            while (pokemon == null)
            {
                var pokemonList = (await session.Inventory.GetPokemons()).ToList();
                pokemonList = pokemonList
                    .Where(w=> !excluded.Contains(w.Id) && w.Id!=session.Profile.PlayerData.BuddyPokemon?.Id)
                    .OrderByDescending(p => p.Cp)
                    .Skip(Math.Min(pokemonList.Count - 1, session.LogicSettings.GymConfig.NumberOfTopPokemonToBeExcluded))
                    .ToList();

                if (pokemonList.Count == 0)
                    return null;

                if (pokemonList.Count == 1) pokemon = pokemonList.FirstOrDefault();
                if (session.LogicSettings.GymConfig.UseRandomPokemon && pokemon == null)
                {
                    pokemon = pokemonList.ElementAt(new Random().Next(0, pokemonList.Count - 1));
                }

                pokemon = pokemonList.FirstOrDefault(p => p.Cp <= session.LogicSettings.GymConfig.MaxCPToDeploy && PokemonInfo.GetLevel(p) <= session.LogicSettings.GymConfig.MaxLevelToDeploy && string.IsNullOrEmpty(p.DeployedFortId));

                if (pokemon.Stamina == 0)
                    await RevivePokemon(session, pokemon);

                if (pokemon.Stamina < pokemon.StaminaMax)
                    await HealPokemon(session, pokemon);

                if (pokemon.Stamina < pokemon.StaminaMax)
                {
                    excluded.Add(pokemon.Id);
                    pokemon = null;
                }
            }
            return pokemon;
        }
    }

}

