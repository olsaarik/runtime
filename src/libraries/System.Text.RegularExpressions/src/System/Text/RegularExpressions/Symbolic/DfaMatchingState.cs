// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Net;
using System.Threading;

namespace System.Text.RegularExpressions.Symbolic
{
    /// <summary>Captures a state of a DFA explored during matching.</summary>
    internal sealed class DfaMatchingState<T> where T : notnull
    {
        internal DfaMatchingState(SymbolicRegexNode<T> node, uint prevCharKind)
        {
            Node = node;
            PrevCharKind = prevCharKind;
        }

        internal SymbolicRegexNode<T> Node { get; }

        internal uint PrevCharKind { get; }

        internal int Id { get; set; }

        internal bool IsInitialState { get; set; }

        /// <summary>State is lazy</summary>
        internal bool IsLazy => Node._info.IsLazy;

        /// <summary>This is a deadend state</summary>
        internal bool IsDeadend => Node.IsNothing;

        /// <summary>The node must be nullable here</summary>
        internal int FixedLength
        {
            get
            {
                if (Node._kind == SymbolicRegexNodeKind.FixedLengthMarker)
                {
                    return Node._lower;
                }

                if (Node._kind == SymbolicRegexNodeKind.Or)
                {
                    Debug.Assert(Node._alts is not null);
                    return Node._alts._maximumLength;
                }

                return -1;
            }
        }

        /// <summary>If true then the state is a dead-end, rejects all inputs.</summary>
        internal bool IsNothing => Node.IsNothing;

        /// <summary>If true then state starts with a ^ or $ or \A or \z or \Z</summary>
        internal bool StartsWithLineAnchor => Node._info.StartsWithLineAnchor;

        /// <summary>
        /// Translates a minterm predicate to a character kind, which is a general categorization of characters used
        /// for cheaply deciding the nullability of anchors.
        /// </summary>
        /// <remarks>
        /// A False predicate is handled as a special case to indicate the very last \n.
        /// </remarks>
        /// <param name="minterm">the minterm to translate</param>
        /// <returns>the character kind of the minterm</returns>
        private uint GetNextCharKind(ref T minterm)
        {
            ICharAlgebra<T> alg = Node._builder._solver;
            T wordLetterPredicate = Node._builder._wordLetterPredicateForAnchors;
            T newLinePredicate = Node._builder._newLinePredicate;

            // minterm == solver.False is used to represent the very last \n
            uint nextCharKind = CharKind.General;
            if (alg.False.Equals(minterm))
            {
                nextCharKind = CharKind.NewLineS;
                minterm = newLinePredicate;
            }
            else if (newLinePredicate.Equals(minterm))
            {
                // If the previous state was the start state, mark this as the very FIRST \n.
                // Essentially, this looks the same as the very last \n and is used to nullify
                // rev(\Z) in the conext of a reversed automaton.
                nextCharKind = PrevCharKind == CharKind.StartStop ?
                    CharKind.NewLineS :
                    CharKind.Newline;
            }
            else if (alg.IsSatisfiable(alg.And(wordLetterPredicate, minterm)))
            {
                nextCharKind = CharKind.WordLetter;
            }
            return nextCharKind;
        }

        /// <summary>
        /// Compute the target state for the given input minterm.
        /// If <paramref name="minterm"/> is False this means that this is \n and it is the last character of the input.
        /// </summary>
        /// <param name="minterm">minterm corresponding to some input character or False corresponding to last \n</param>
        internal DfaMatchingState<T> Next(T minterm)
        {
            uint nextCharKind = GetNextCharKind(ref minterm);

            // Combined character context
            uint context = CharKind.Context(PrevCharKind, nextCharKind);

            // Compute the derivative of the node for the given context
            SymbolicRegexNode<T> derivative = Node.CreateDerivativeWithEffects(eager: true).TransitionOrdered(minterm, context);

            // nextCharKind will be the PrevCharKind of the target state
            // use an existing state instead if one exists already
            // otherwise create a new new id for it
            return Node._builder.CreateState(derivative, nextCharKind, capturing: false);
        }

        /// <summary>
        /// Compute a set of transitions for the given minterm.
        /// </summary>
        /// <param name="minterm">minterm corresponding to some input character or False corresponding to last \n</param>
        /// <returns>an enumeration of the transitions as pairs of the target state and a list of effects to be applied</returns>
        internal List<(DfaMatchingState<T> derivative, List<DerivativeEffect> effects)> AntimirovEagerNextWithEffects(T minterm)
        {
            uint nextCharKind = GetNextCharKind(ref minterm);

            // Combined character context
            uint context = CharKind.Context(PrevCharKind, nextCharKind);

            // Compute the transitions for the given context
            IEnumerable<(SymbolicRegexNode<T>, List<DerivativeEffect>)> derivativesAndEffects =
                Node.CreateDerivativeWithEffects(eager: true).TransitionsWithEffects(minterm, context);

            var list = new List<(DfaMatchingState<T> derivative, List<DerivativeEffect> effects)>();
            foreach ((SymbolicRegexNode<T> derivative, List<DerivativeEffect> effects) in derivativesAndEffects)
            {
                // nextCharKind will be the PrevCharKind of the target state
                // use an existing state instead if one exists already
                // otherwise create a new new id for it
                list.Add((Node._builder.CreateState(derivative, nextCharKind, capturing: true), effects));
            }
            return list;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool IsNullable(uint nextCharKind)
        {
            Debug.Assert(nextCharKind is 0 or CharKind.StartStop or CharKind.Newline or CharKind.WordLetter or CharKind.NewLineS);
            uint context = CharKind.Context(PrevCharKind, nextCharKind);
            return Node.IsNullableFor(context);
        }

        public override bool Equals(object? obj) =>
            obj is DfaMatchingState<T> s && PrevCharKind == s.PrevCharKind && Node.Equals(s.Node);

        public override int GetHashCode() => (PrevCharKind, Node).GetHashCode();

        public override string ToString() =>
            PrevCharKind == 0 ? Node.ToString() :
             $"({CharKind.DescribePrev(PrevCharKind)},{Node})";

#if DEBUG
        internal string DgmlView
        {
            get
            {
                string info = CharKind.DescribePrev(PrevCharKind);
                if (info != string.Empty)
                {
                    info = $"Previous: {info}&#13;";
                }

                string deriv = WebUtility.HtmlEncode(Node.ToString());
                if (deriv == string.Empty)
                {
                    deriv = "()";
                }

                return $"{info}{deriv}";
            }
        }
#endif
    }

    /// <summary>Encapsulates either a DFA state in Brzozowski mode or an NFA state set in Antimirov mode. Used by reference only.</summary>
    internal struct CurrentState<T> where T : notnull
    {
        // TBD: Consider SparseIntMap instead of HashSet
        // TBD: OrderedOr

        /// <summary>The associated builder used to lazily add new DFA or NFA nodes to the graph.</summary>
        private readonly SymbolicRegexBuilder<T> _builder;

        /// <summary>The DFA matching state instance.</summary>
        /// <remarks>null when in Antimirov (NFA) mode.</remarks>
        private DfaMatchingState<T>? _dfaMatchingState;

        /// <summary>The set of NFA states.</summary>
        internal readonly HashSet<int>? _nfaStates;
        /// <summary>The list of NFA states, in order.</summary>
        /// <remarks>
        /// The contents of this list is the same as <see cref="_nfaStates"/>, but it's used for maintaining
        /// the order of the states whereas <see cref="_nfaStates"/> is used for O(1) lookup.
        /// </remarks>
        internal List<int>? _nfaStatesList;
        /// <summary>Scratch list used temporarily to maintain the previous list of states.</summary>
        internal List<int>? _nfaStatesListScratch;

        public CurrentState(DfaMatchingState<T> dfaMatchingState, SymbolicRegexMatcher.PerThreadData perThreadData)
        {
            _builder = dfaMatchingState.Node._builder;

            if (_builder._antimirov)
            {
                // The builder is in NFA mode.

                // Initialize _nfaStates, _nfaStatesList, and _nfaStatesListScratch  from the PerThreadData cache.
                // We want to create these lists/sets once per runner, and so pass around the PerThreadData that
                // caches them, enabling each CurrentState instance to use those cached objects.  For _nfaStates
                // and _nfaStatesList, we need to clear the collections in case they were left with data in them.
                // For _nfaStatesListScratch, it'll be cleared before it's used later and thus we needn't clear it now.

                _nfaStates = perThreadData.NfaStateSet;
                if (_nfaStates is not null)
                {
                    _nfaStates.Clear();
                }
                else
                {
                    _nfaStates = perThreadData.NfaStateSet = new();
                }

                _nfaStatesList = perThreadData.NfaStateList;
                if (_nfaStatesList is not null)
                {
                    _nfaStatesList.Clear();
                }
                else
                {
                    _nfaStatesList = perThreadData.NfaStateList = new();
                }

                _nfaStatesListScratch = perThreadData.NfaStateListScratch ??= new();

                // Create NFA state set.
                Debug.Assert(dfaMatchingState.Node.Kind == SymbolicRegexNodeKind.Or && dfaMatchingState.Node._alts is not null);
                foreach (SymbolicRegexNode<T> member in dfaMatchingState.Node._alts)
                {
                    // Create (possibly new) NFA states for all the members.
                    // Add their IDs to the current set of NFA states and into the list.
                    int nfaState = _builder.CreateNfaState(member, dfaMatchingState.PrevCharKind);
                    if (_nfaStates.Add(nfaState))
                    {
                        // The list maintains the original order in which states are added (without duplicates).
                        // TBD: OrderedOr may need to rely on that order.
                        _nfaStatesList.Add(nfaState);
                    }
                }

                // Store null into _dfaMatchingState to indicate we're in NFA mode.
                _dfaMatchingState = null;
            }
            else
            {
                _nfaStates = null;
                _nfaStatesList = null;
                _nfaStatesListScratch = null;

                // Brzozowski mode
                _dfaMatchingState = dfaMatchingState;
            }
        }

        public bool StartsWithLineAnchor
        {
            get
            {
                if (_dfaMatchingState is null)
                {
                    // In Antimirov mode, check if any underlying core state starts with a line anchor.
                    List<int> states = _nfaStatesList!;
                    for (int i = 0; i < states.Count; i++)
                    {
                        if (_builder.GetCoreState(states[i]).StartsWithLineAnchor)
                        {
                            return true;
                        }
                    }

                    return false;
                }
                else
                {
                    // Brzozowski mode
                    return _dfaMatchingState.StartsWithLineAnchor;
                }
            }
        }

        public bool IsNullable(uint nextCharKind)
        {
            if (_dfaMatchingState is null)
            {
                // In Antimirov mode, check if any underlying core state is nullable.
                List<int> states = _nfaStatesList!;
                for (int i = 0; i < states.Count; i++)
                {
                    if (_builder.GetCoreState(states[i]).IsNullable(nextCharKind))
                    {
                        return true;
                    }
                }

                return false;
            }
            else
            {
                return _dfaMatchingState.IsNullable(nextCharKind);
            }
        }

        /// <summary>Gets whether this is a dead-end state, meaning there are no transitions possible out of the state.</summary>
        /// <remarks>In Antimirov mode, an empty set of states means that it is a deadend.</remarks>
        public bool IsDeadend => _dfaMatchingState?.IsDeadend ?? _nfaStates!.Count == 0;

        /// <summary>Gets whether this is a nothing state, meaning it doesn't match.</summary>
        /// <remarks>In Antimirov mode, an empty set of states means that it is nothing.</remarks>
        public bool IsNothing => _dfaMatchingState?.IsNothing ?? _nfaStates!.Count == 0;

        /// <summary>Gets the length of any fixed-length marker that exists for this state, or -1 if there is none.</summary>
        /// <summary>In Antimirov mode, there are no fixed-length markers.</summary>
        public int FixedLength => _dfaMatchingState?.FixedLength ?? -1;

        /// <summary>Gets whether this is an initial state.</summary>
        /// <summary>In Antimirov mode, no set of states qualifies as an initial state.</summary>
        public bool IsInitialState => _dfaMatchingState?.IsInitialState ?? false;

        /// <summary>Take the transition to the next state.</summary>
        /// <remarks>This may cause a shift from  Brzozowski to Antimirov mode.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void TakeTransition(ref CurrentState<T> state, int mintermId, T minterm, SymbolicRegexMatcher.PerThreadData perThreadData)
        {
            SymbolicRegexBuilder<T> builder = state._builder;

            if (state._dfaMatchingState is DfaMatchingState<T> dfaMatchingState)
            {
                Debug.Assert(builder._delta is not null);

                int dfaOffset = (dfaMatchingState.Id << builder._mintermsCount) | mintermId;
                state._dfaMatchingState = dfaMatchingState =
                    Volatile.Read(ref builder._delta[dfaOffset]) ??
                    builder.CreateNewTransition(dfaMatchingState, minterm, dfaOffset);

                if (builder._antimirov)
                {
                    // CreateNewTransition switched from Brzozowski to Antimirov mode.
                    // Update the state representation accordingly.
                    // TBD: OrderedOr
                    Debug.Assert(dfaMatchingState.Node.Kind == SymbolicRegexNodeKind.Or);
                    state = new CurrentState<T>(dfaMatchingState, perThreadData);
                }
            }
            else
            {
                // Grab the sets/lists, swapping the current active states list with the scratch list.
                HashSet<int> destStates = state._nfaStates!;
                List<int> destStatesList = state._nfaStatesListScratch!;
                List<int> sourceStates = state._nfaStatesList!;
                state._nfaStatesList = destStatesList;
                state._nfaStatesListScratch = sourceStates;

                // Transition into the new set of target NFA states.
                destStates.Clear();
                destStatesList.Clear();
                for (int i = 0; i < sourceStates.Count; i++)
                {
                    int source = sourceStates[i];

                    // Calculate the offset into the NFA transition table.
                    int nfaOffset = (source << builder._mintermsCount) | mintermId;
                    List<int> targets =
                        Volatile.Read(ref builder._antimirovDelta[nfaOffset]) ??
                        builder.CreateNewNfaTransition(source, mintermId, minterm, nfaOffset);

                    // Add each non-duplicate target to the states list.
                    for (int j = 0; j < targets.Count; j++)
                    {
                        if (destStates.Add(targets[j]))
                        {
                            destStatesList.Add(targets[j]);
                        }
                    }
                }
            }
        }
    }
}
