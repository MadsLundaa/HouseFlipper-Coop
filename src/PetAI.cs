using System;
using System.Linq;
using System.Reflection;
using System.Text;
using FrozenWay.HouseFlipper.Pets;
using UnityEngine;

namespace HFCoop
{
    /// <summary>
    /// Pausing and resuming a pet's own AI (PetBehaviourManager) and navigation. Switching the behaviour manager off clears its "initialized" flag (its OnDisable) and switching it back on does
    /// not set it again, so a pet whose AI had been paused once lost its interaction state for good: nobody could pick it up, pet it or feed it. Resuming therefore runs the manager's own
    /// Initialize(pet, walkable area manager, nav agent) again with the values it had.
    /// </summary>
    internal static class PetAI
    {
        static FieldInfo _areaF, _navF, _petF; static MethodInfo _init, _unsub; static PropertyInfo _isSetUp; static bool _resolved;

        static void Resolve(Type mt)
        {
            if (_resolved) return; _resolved = true;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _init = mt.GetMethods(F).FirstOrDefault(m => m.Name == "Initialize" && m.GetParameters().Length == 3);
            if (_init == null) { Plugin.Log.LogWarning("[petai] PetBehaviourManager.Initialize not found: a resumed pet may not take interactions"); return; }
            // whether the manager is set up (an obfuscated bool property), and its own "unsubscribe" step: the parameterless method that only removes event handlers (what its OnDisable runs).
            // Initialize subscribes the manager to three events, among them a static scene-change event; running it on a manager that is still subscribed added a second subscription,
            // and the one left behind when the pet was destroyed broke the next house load (the game's scene loader stopped on it: the house never finished loading, so no animals).
            _isSetUp = mt.GetProperty("تٜٙجهچ", F);
            // the subscribe step (Initialize calls it) adds three handlers; its counterpart removes the same three. Found by what they call: the parameterless method that only
            // removes handlers and removes the most of them, preferring the shortest such method (OnDestroy-like methods do more)
            int best = 0, bestLen = int.MaxValue; var seen = new StringBuilder();
            foreach (var m in mt.GetMethods(F | BindingFlags.DeclaredOnly))
            {
                if (m.GetParameters().Length != 0 || m.ReturnType != typeof(void)) continue;
                byte[] il; try { var body = m.GetMethodBody(); il = body == null ? null : body.GetILAsByteArray(); } catch { continue; }
                if (il == null) continue; int removes = 0, adds = 0;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] != 0x28 && il[i] != 0x6F) continue;
                    try { var cb = mt.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)); if (cb == null) continue; if (cb.Name.StartsWith("remove_")) removes++; else if (cb.Name.StartsWith("add_")) adds++; } catch { }
                }
                if (removes > 0) seen.Append(" [" + removes + "-/" + adds + "+ " + il.Length + "b]");
                if (adds == 0 && removes >= 3 && (removes > best || (removes == best && il.Length < bestLen))) { _unsub = m; best = removes; bestLen = il.Length; }
            }
            if (_unsub == null) Plugin.Log.LogWarning("[petai] the AI's unsubscribe step was not found (methods that remove handlers:" + seen + "): resuming a pet may leave an extra event subscription");
            else Plugin.Log.LogInfo("[petai] AI unsubscribe step found (" + best + " handlers, " + bestLen + " bytes)");
            var ps = _init.GetParameters();
            foreach (var f in mt.GetFields(F))
            {
                if (f.FieldType == ps[1].ParameterType && _areaF == null) _areaF = f;
                else if (f.FieldType == ps[2].ParameterType && _navF == null) _navF = f;
                else if (f.FieldType == ps[0].ParameterType && _petF == null) _petF = f;
            }
        }

        // The interaction controller keeps the AI's interaction-state action once it has looked it up; setting the AI up again makes new actions, and the kept one (from before) no longer
        // runs, so the first take-over worked and later ones did not. Those kept references are cleared, and the controller looks the current action up on its next interaction.
        static MethodInfo _getAction;
        static void ForgetOldActions(Pet p, Behaviour ai)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (_getAction == null) _getAction = ai.GetType().GetMethod("TryToGetPetAction", F);
            if (_getAction == null) return;
            var actionT = _getAction.ReturnType; int n = 0;
            foreach (var c in p.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null || c == ai) continue; var tn = c.GetType().Name; if (!tn.Contains("Interaction") && !tn.Contains("Petting") && !tn.Contains("PickUp") && !tn.Contains("Snack")) continue;
                for (var ty = c.GetType(); ty != null && ty != typeof(MonoBehaviour); ty = ty.BaseType)
                    foreach (var f in ty.GetFields(F | BindingFlags.DeclaredOnly))
                        if (actionT.IsAssignableFrom(f.FieldType) && f.GetValue(c) != null) { f.SetValue(c, null); n++; }
            }
            if (n > 0) Plugin.Log.LogInfo("[petai] " + p.name + ": " + n + " kept AI action reference(s) cleared");
        }

        // Small animals (hamster, guinea pig, rat, parrot, snake, spider, turtle, fish) do not use PetBehaviourManager: their own behaviour (a SmallAnimalBehaviour) walks them along the
        // nodes of their cage. It has no set-up that switching it off would clear, so it is simply paused and resumed.
        static Behaviour SmallAI(Pet p)
        {
            foreach (var c in p.GetComponentsInChildren<Behaviour>(true))
                for (var t = c == null ? null : c.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType) if (t.Name == "SmallAnimalBehaviour") return c;
            return null;
        }

        public static void Pause(Pet p)
        {
            if (p == null) return;
            var ai = p.GetComponent("PetBehaviourManager") as Behaviour; if (ai != null && ai.enabled) ai.enabled = false;
            var nav = p.GetComponent("PetNavAgent") as Behaviour; if (nav != null && nav.enabled) nav.enabled = false;
            var ag = p.GetComponent<UnityEngine.AI.NavMeshAgent>(); if (ag != null && ag.enabled) ag.enabled = false;
            var small = SmallAI(p); if (small != null && small.enabled) small.enabled = false;
        }

        /// <summary>While a pet follows another game: switch off again what the game switched back on (its walkable-area manager turns the navigation of every pet back on when the areas change).</summary>
        public static void KeepPaused(Pet p)
        {
            if (p == null) return;
            var ai = p.GetComponent("PetBehaviourManager") as Behaviour; var nav = p.GetComponent("PetNavAgent") as Behaviour; var ag = p.GetComponent<UnityEngine.AI.NavMeshAgent>(); var small = SmallAI(p);
            if ((ai != null && ai.enabled) || (nav != null && nav.enabled) || (ag != null && ag.enabled) || (small != null && small.enabled)) Pause(p);
        }

        public static void Resume(Pet p)
        {
            if (p == null) return;
            var ag = p.GetComponent<UnityEngine.AI.NavMeshAgent>(); if (ag != null) { ag.enabled = true; if (ag.isOnNavMesh) ag.Warp(p.transform.position); }
            var nav = p.GetComponent("PetNavAgent") as Behaviour; if (nav != null) nav.enabled = true;
            var small = SmallAI(p); if (small != null) small.enabled = true;
            var ai = p.GetComponent("PetBehaviourManager") as Behaviour; if (ai == null) return;
            ai.enabled = true;
            try
            {
                Resolve(ai.GetType()); if (_init == null) return;
                if (_isSetUp != null && (bool)_isSetUp.GetValue(ai, null)) return;   // still set up (it was never really paused): nothing to redo
                if (_unsub != null) { try { _unsub.Invoke(ai, null); } catch { } }    // no subscriptions left over before Initialize adds them
                object pet = _petF != null ? _petF.GetValue(ai) : null; if (pet == null) pet = p;
                object area = _areaF != null ? _areaF.GetValue(ai) : null; object navA = _navF != null ? _navF.GetValue(ai) : null;
                if (navA == null) navA = nav;
                _init.Invoke(ai, new[] { pet, area, navA });
                ForgetOldActions(p, ai);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[petai] could not set " + p.name + "'s AI up again: " + (e.InnerException ?? e).Message); }
        }
    }
}
