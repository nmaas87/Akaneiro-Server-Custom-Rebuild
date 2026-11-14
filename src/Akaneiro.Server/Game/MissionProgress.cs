using System;
using System.Collections.Generic;
using System.Linq;

namespace Akaneiro.Server.Game
{
    /// <summary>
    /// Tracks how far a player is through the mission they accepted. The client counts objectives
    /// for its own HUD as well (PlayerDataManager.CheckMissionProgress); the server decides when a
    /// task, a branch or the whole mission counts as done and hands out the rewards.
    /// </summary>
    public sealed class MissionProgress
    {
        private sealed class TaskState
        {
            public MissionTask Task;
            public int BranchIndex;
            public int[] Counts;
            public bool Completed;
        }

        private readonly List<TaskState> _tasks = new List<TaskState>();

        public MissionDefinition Mission { get; }

        public MissionProgress(MissionDefinition mission)
        {
            Mission = mission;
            for (int branch = 0; branch < mission.Branches.Count; branch++)
            {
                foreach (MissionTask task in mission.Branches[branch].Tasks)
                    _tasks.Add(new TaskState { Task = task, BranchIndex = branch, Counts = new int[task.Objectives.Count] });
            }
        }

        /// <summary>
        /// The mission is done when every regular branch is; bonus branches are extra. A mission
        /// made of bonus branches only (Gashadokuro!, Without a Head) needs all of them. The client
        /// has no rule of its own, it just shows what MissionCompleteNTF says.
        /// </summary>
        public bool IsComplete
        {
            get
            {
                bool anyRegular = Mission.Branches.Any(b => b.IsBonus == 0);
                return _tasks.Where(t => !anyRegular || Mission.Branches[t.BranchIndex].IsBonus == 0).All(t => t.Completed);
            }
        }

        public sealed record Completion(int BranchIndex, MissionBranch Branch, MissionTask Task, bool BranchCompleted);

        /// <summary>
        /// Counts one event (a kill, a pickup, ...) and returns the tasks it completed. Like the
        /// client (_UI_CS_MissionLogic.CheckMissionProgress), only the current task of each branch
        /// counts: the tasks of a branch are done one after the other.
        /// </summary>
        public List<Completion> Record(int objectiveType, int objectId)
        {
            var completed = new List<Completion>();
            var currentTasks = _tasks.GroupBy(t => t.BranchIndex)
                .Select(branch => branch.FirstOrDefault(t => !t.Completed))
                .Where(t => t != null)
                .ToList();
            foreach (TaskState state in currentTasks)
            {
                bool changed = false;
                for (int i = 0; i < state.Task.Objectives.Count; i++)
                {
                    MissionObjective objective = state.Task.Objectives[i];
                    if (objective.TypeId != objectiveType || objective.ObjectId != objectId || state.Counts[i] >= objective.Count)
                        continue;
                    state.Counts[i]++;
                    changed = true;
                }

                if (!changed)
                    continue;

                bool done = state.Task.Objectives
                    .Select((objective, i) => state.Counts[i] >= objective.Count)
                    .All(x => x);
                if (!done)
                    continue;

                state.Completed = true;
                MissionBranch branch = Mission.Branches[state.BranchIndex];
                bool branchDone = _tasks.Where(t => t.BranchIndex == state.BranchIndex).All(t => t.Completed);
                completed.Add(new Completion(state.BranchIndex, branch, state.Task, branchDone));
            }
            return completed;
        }
    }
}
