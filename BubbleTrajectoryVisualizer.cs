#if UNITY_EDITOR

using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using com.pigsels.tools;
using UnityEditor;
using Random = UnityEngine.Random;

namespace com.pigsels.BubbleTrouble
{
    /// <summary>
    /// Draws Bubble trajectories and controls in the Scene view if HexGridMode
    /// is disabled in actual merged game config (provided by GameConfigEditorLoader).
    /// 
    /// This component is intended to exist in Editor-time only. It's not available in Play-time.
    /// It's dynamically instantiated by EditorLevelSceneKeeper when a Level Scene is loaded and
    /// is prevented to be saved in the Scene file.
    /// </summary>
    [ExecuteInEditMode]
    public class BubbleTrajectoryVisualizer : MonoBehaviour
    {

        private bool IsInit;

        /// <summary>
        /// Contains actual trajectories for all bubbles in scene.
        /// </summary>
        private static Dictionary<BubbleConfig, Vector2[]> CachedTrajectories;

        /// <summary>
        /// All calculations are done in separate thread to prevent main thread from lags.
        /// </summary>
        private static Thread TrajectoryCalculationThread;

        public static bool ShowedBubbleOutOfEncounterWarning;

        private BubbleConfig SelectedBubbleConfig;

        /// <summary>
        /// Dispatcher is needed to simplify calls of unity API from not main thread
        /// </summary>
        private static MainThreadDispatcher mainThreadDispatcher;

        /// <summary>
        /// Contains info about all trajectory intersections
        /// </summary>
        private static List<Tuple<Vector2, List<BubbleConfig>, int>> Intersections;

        public static float EditorTrajectoryLength
        {
            get => EditorPrefs.GetFloat(TrajectoryLengthPrefKey, 1.0f);
            set => EditorPrefs.SetFloat(TrajectoryLengthPrefKey, value);
        }
        private static string TrajectoryLengthPrefKey => EditorPrefsHelper.GetEditorPrefsKey(typeof(BubbleConfig), "TrajectoryLength");

        private static bool ShowOtherTrajectories;

        private static bool ShowOtherTrajectoriesPrevious;

        private static bool ShowCalculationMessage;

        private bool ShowIntersections = true;

        private static bool IsMultiselected => Selection.gameObjects.Count(obj => (obj).GetComponent<BubbleConfig>() != null) > 1;

        private static GameConfigSO ActualGameConfig => EditorLevelSceneKeeper.GetActualMergedLevelConfig();        
        private static bool IsHexGridMode => ActualGameConfig.HexGridAlignment;

        private EncounterSO currentSelectedEncounter;

        private void OnEnable()
        {
            Init();
        }

        private void OnDestroy()
        {
            Deinit();
        }

        private void OnDisable()
        {
            Deinit();
        }

        private void Init()
        {
            if (Application.isPlaying)
            {
                Destroy(this);
                return;
            }

            if (IsInit) return;

            GameConfigEditorLoader.OnGameConfigChanged += OnGameConfigChanged;
            SceneView.duringSceneGui += OnScene;

            Undo.willFlushUndoRecord += UndoWillFlushUndoRecordCallback;
            Undo.undoRedoPerformed += UndoRedoPerformedCallback;

            mainThreadDispatcher = MainThreadDispatcher.Instance;

            Selection.selectionChanged += OnSelectionChanged;

            IsInit = true;
        }

        private void Deinit()
        {
            if (!IsInit) return;

            GameConfigEditorLoader.OnGameConfigChanged -= OnGameConfigChanged;
            SceneView.duringSceneGui -= OnScene;

            Undo.willFlushUndoRecord -= UndoWillFlushUndoRecordCallback;
            Undo.undoRedoPerformed -= UndoRedoPerformedCallback;

            Selection.selectionChanged -= OnSelectionChanged;

            IsInit = false;
        }


        /// <summary>
        /// Gets currently selected BubbleConfig.
        /// </summary>
        /// <returns>If no BubbleConfig is selected returns null. Otherwise returns first BubbleConfig of all BubbleConfigs in selections</returns>
        private BubbleConfig GetCurrentSelectedBubbleConfig()
        {
            BubbleConfig[] bubbleConfigs = Selection.GetFiltered<BubbleConfig>(SelectionMode.Editable | SelectionMode.ExcludePrefab | SelectionMode.TopLevel);

            if (bubbleConfigs.Length > 0)
            {
                return bubbleConfigs[0];
            }

            return null;
        }

        /// <summary>
        /// Gets the encounter that gameobject belongs to.
        /// </summary>
        /// <param name="obj"></param>
        /// <returns>Encounter of the obj or null if no encounter was found</returns>
        private EncounterSO GetGameObjectsEncounter(GameObject obj)
        {
            var encounters = EditorLevelSceneKeeper.GetLevelConfig().Encounters;

            for (int i = 0; i < encounters.Count; i++)
            {
                if (encounters[i].Zone.Contains((Vector2)obj.transform.position))
                {
                    return encounters[i];

                }
            }

            return null;
        }

        /// <summary>
        /// Recalculates trajectories of bubbles in selected encounter. Shows message about recalculation to user on screen. Repaints trajectories if the calculations where succeed.
        /// </summary>
        private void RecalculateAndRepaintTrajectories()
        {
            //Debug.Log("Updating trajectories");
            if (!IsMultiselected)
            {
                ShowCalculationMessage = true;
                RecalculateTrajectories(OnRecalculationComplete);
            }
        }

#region Callbacks

        private void OnDrawGizmos()
        {
            if (Application.isPlaying || !isActiveAndEnabled) return;
            if (IsHexGridMode) return;

            //DrawTrajectories();

            if (ShowIntersections)
            {
                DrawIntersections();
            }
        }
        private void UndoWillFlushUndoRecordCallback()
        {
            //Debug.Log($"Undo will flush callback {TargetBubbleConfig.transform.position}");
            OnSceneModified();
        }

        private void UndoRedoPerformedCallback()
        {
            //Debug.Log($"Undo redo callback {TargetBubbleConfig.transform.position}");
            OnSceneModified();
        }

        private void OnSceneModified()
        {
            if (SelectedBubbleConfig != null && !IsHexGridMode)
            {
                RecalculateAndRepaintTrajectories();
            }
        }

        private void OnScene(SceneView sceneView)
        {
            if (Application.isPlaying) return;
            if (IsHexGridMode) return;

            DrawTrajectoryControls();
        }

        /// <summary>
        /// Called when actual merged level game config changes.
        /// </summary>
        private void OnGameConfigChanged()
        {
            if (SelectedBubbleConfig != null && !IsHexGridMode)
            {
                RecalculateAndRepaintTrajectories();
            }
        }

        private void OnSelectionChanged()
        {
            BubbleConfig prevSelectedBubbleConfig = SelectedBubbleConfig;


            SelectedBubbleConfig = GetCurrentSelectedBubbleConfig();

            //Debug.Log($"Selection changed {TargetBubbleConfig}");

            if (SelectedBubbleConfig != null)
            {
                ShowIntersections = true;

                EncounterSO lastSelectedEncounter = currentSelectedEncounter;

                currentSelectedEncounter = GetGameObjectsEncounter(SelectedBubbleConfig.gameObject);

                if (prevSelectedBubbleConfig == null || lastSelectedEncounter != currentSelectedEncounter) // selected bubble belong to not calculated encounter.
                    // Need to recalculate trajectories
                {
                    ClearAllTrajectories();
                    RecalculateAndRepaintTrajectories();
                }
                else
                {
                    //Debug.Log("Selected bubble from same encounter. Not doing update");
                    if (!ShowOtherTrajectories) //Bubble was selected from the same encounter as previous selected bubble.
                        //Need just to hide previous trajectory and show new one.
                    {
                        ClearTrajectory(prevSelectedBubbleConfig);

                        DrawTrajectory(SelectedBubbleConfig, GetBubbleTrajectoryColor(SelectedBubbleConfig));
                    }
                }
            }
            else //No bubble is selected. Hiding all trajectories
            {
                ShowIntersections = false;
                ClearAllTrajectories();
            }
        }

        /// <summary>
        /// Handle completion of the trajectory recalculations
        /// </summary>
        private void OnRecalculationComplete()
        {
            ShowCalculationMessage = false;

            if (SelectedBubbleConfig != null)
            {
                DrawAllRequestedTrajectories();
            }
        }

#endregion

#region Controlls drawing

        private void DrawTimeLimitButton()
        {
            Handles.BeginGUI();
            GUILayout.BeginVertical();
            GUILayout.BeginArea(new Rect(20, 60, 140, 300));

            float prevTrajTime = EditorTrajectoryLength;

            GUILayout.Label("Trajectory length", "box");
            EditorTrajectoryLength = GUILayout.HorizontalSlider(EditorTrajectoryLength, 0, 5, GUILayout.MinHeight(20));

            GUILayout.EndArea();
            GUILayout.EndVertical();
            Handles.EndGUI();

            if (prevTrajTime != EditorTrajectoryLength)
            {
                OnSceneModified();
            }
        }

        private void DrawCalculationMessage()
        {
            float width = 140;

            Handles.BeginGUI();
            GUILayout.BeginVertical();

            GUILayout.BeginArea(new Rect(SceneView.lastActiveSceneView.position.width - width - 20, 20, width, 300));

            GUIStyle style = new GUIStyle(GUI.skin.box);

            Color defaultGUIBackgroundColor = GUI.backgroundColor;

            GUI.backgroundColor = new Color(1, 0, 0, 0.5f);


            style.normal.textColor = Color.white;

            GUILayout.Label("Calculating trajectories", style);

            GUI.backgroundColor = defaultGUIBackgroundColor;

            GUILayout.EndArea();
            GUILayout.EndVertical();
            Handles.EndGUI();
        }

        /// <summary>
        /// Draws button over the scene
        /// </summary>
        private void DrawAllTrajectoryButton()
        {
            //Debug.Log("Drawing all trajectory button " + this.target);
            Handles.BeginGUI();
            GUILayout.BeginVertical();
            GUILayout.BeginArea(new Rect(20, 20, 140, 300));

            ShowOtherTrajectories = GUILayout.Toggle(ShowOtherTrajectories, "Show all trajectories ", "button", GUILayout.MinHeight(20));

            GUILayout.EndArea();
            GUILayout.EndVertical();
            Handles.EndGUI();

            if (ShowOtherTrajectories != ShowOtherTrajectoriesPrevious)
            {
                if (SelectedBubbleConfig != null)
                {
                    if (ShowOtherTrajectories)
                    {
                        DrawAllRequestedTrajectories();
                    }
                    else
                    {
                        ClearAllTrajectories();
                        DrawTrajectory(SelectedBubbleConfig, GetBubbleTrajectoryColor(SelectedBubbleConfig));
                    }
                }

                ShowOtherTrajectoriesPrevious = ShowOtherTrajectories;
            }
        }

        /// <summary>
        /// Draws trajectories controls.
        /// </summary>
        private void DrawTrajectoryControls()
        {
            if (IsHexGridMode)
            {
                ShowOtherTrajectories = false;
                return;
            }

            if (!IsMultiselected)
            {

                DrawAllTrajectoryButton();
                DrawTimeLimitButton();

                if (ShowCalculationMessage)
                {
                    DrawCalculationMessage();
                }
            }
            else
            {
                Handles.BeginGUI();

                GUILayout.BeginArea(new Rect(5, 5, 500, 200));

                var lbl = new GUIContent("Bubble trajectories do not support multiple selection");
                EditorGUI.DrawRect(new Rect(0, 0, GUI.skin.label.CalcSize(lbl).x, 20), new Color(0, 0, 0, .5f));
                GUILayout.Label(lbl, EditorStyles.whiteLabel);

                GUILayout.Space(5);

                GUILayout.EndArea();

                Handles.EndGUI();
            }
        }

#endregion

#region Trajectories drawing

        /// <summary>
        /// Draw bubble trajectory base on its BubbleConfig.
        /// </summary>
        /// <param name="bubble"></param>
        public void DrawTrajectory(BubbleConfig bubble, Color trajectoryColor)
        {
            //Debug.Log("Showing trajectory");

            // dont draw anythis if no BubbleDefinition set.
            if (bubble.bubbleDefinition?.editorAsset == null)
            {
                //HideTrajectory(bubble);
                return;
            }

            //if (!CachedTrajectories.ContainsKey(this))
            //{
            //    Debug.LogWarning($"No key {this} found");
            //    return;
            //}

            LineRenderer lineRenderer = bubble.GetComponent<LineRenderer>();

            if (lineRenderer == null) return;

            if (CachedTrajectories == null || !CachedTrajectories.ContainsKey(bubble))
            {
                lineRenderer.positionCount = 0;
                return;
            }

            lineRenderer.startColor = trajectoryColor;
            lineRenderer.endColor = trajectoryColor;

            Vector2[] points = CachedTrajectories[bubble];

            Vector3[] convertedPoints = Array.ConvertAll<Vector2, Vector3>(points, v2 => v2);

            int pointsLength;

            if (Intersections.Count > 0)
            {
                pointsLength = Intersections[0].Item3;
            }
            else
            {
                pointsLength = points.Length;
            }

            lineRenderer.positionCount = pointsLength;
            lineRenderer.SetPositions(convertedPoints);
            lineRenderer.Simplify(0.01f);

            //Debug.Log("Showed trajectory. Line renderer points count = " + lineRenderer.positionCount);

        }

        /// <summary>
        /// Hide bubble's trajectory.
        /// </summary>
        /// <param name="bubble"></param>
        public void ClearTrajectory(BubbleConfig bubble)
        {
            LineRenderer lineRenderer = bubble.GetComponent<LineRenderer>();

            if (lineRenderer != null)
            {
                lineRenderer.SetPositions(new Vector3[0]);
                lineRenderer.positionCount = 0;
            }
        }

        /// <summary>
        /// Clear all bubble's trajectories.
        /// </summary>
        private void ClearAllTrajectories()
        {
            List<BubbleConfig> currentBubbles = new List<BubbleConfig>(GameObject.FindObjectsOfType<BubbleConfig>());

            foreach (var bubble in currentBubbles)
            {
                ClearTrajectory(bubble);
            }
        }

        /// <summary>
        /// Draws all requested trajectories. Uses <see cref="ShowOtherTrajectories"/> to determine trajectories to draw.
        /// </summary>
        public void DrawAllRequestedTrajectories()
        {
            //Debug.Log("showing all trajectories");

            List<BubbleConfig> currentBubbles = new List<BubbleConfig>(GameObject.FindObjectsOfType<BubbleConfig>());
            if (ShowOtherTrajectories)
            {
                foreach (var bubble in currentBubbles)
                {
                    if (bubble.bubbleDefinition?.editorAsset == null)
                    {
                        continue;
                    }

                    Color trajectoryColor = GetBubbleTrajectoryColor(bubble);

                    if (bubble != SelectedBubbleConfig)
                    {
                        //Make unselected bubbles transparent
                        trajectoryColor = new Color(trajectoryColor.r, trajectoryColor.g, trajectoryColor.b, 0.75f);
                    }

                    DrawTrajectory(bubble, trajectoryColor);
                }
            }
            else
            {
                DrawTrajectory(SelectedBubbleConfig, GetBubbleTrajectoryColor(SelectedBubbleConfig));
            }
        }

        private Color GetBubbleTrajectoryColor(BubbleConfig BubbleConfig)
        {
            if (BubbleConfig.bubbleDefinition.editorAsset != null &&
                BubbleConfig.IsBubbleDefinitionAllowedInConfig(BubbleConfig.bubbleDefinition))
            {
                return BubbleConfig.bubbleDefinition.editorAsset.pureColor;
            }
            else
            {
                return Color.white;
            }
        }

        /// <summary>
        /// Draws intersections of two bubble's. Draws in gizmos. Circle layers colors describe colors of intersection bubbles.
        /// </summary>
        private void DrawIntersections()
        {
            if (Intersections == null)
            {
                return;
            }

            foreach (var intersection in Intersections)
            {
                float startCircleRadius = 0.5f;
                float circleRadius = startCircleRadius;

                for (int i = 0; i < intersection.Item2.Count; i++)
                {
                    Color sphereColor = GetBubbleTrajectoryColor(intersection.Item2[i]);
                    Gizmos.color = new Color(sphereColor.r,
                        sphereColor.g,
                        sphereColor.b,
                        1);

                    Gizmos.DrawSphere(intersection.Item1, circleRadius);
                    circleRadius -= 0.3f * startCircleRadius;
                }
                Gizmos.color = Color.black;
                Gizmos.DrawSphere(intersection.Item1, circleRadius);
            }
        }

#endregion

#region Tracjectories calculations

    private List<Vector2[]> CalculatePathsParallel(
            float endTime,
            List<Vector2> startPos,
            List<float> bubblesMass,
            List<Vector2> attractorsPositions,
            List<float> attractorsMasses,
            List<float> gravityScales,
            List<float> linearDrags,
            List<Vector2> speedVector,
            Vector2 gravitation,
            float attractorFactor,
            float sameColorFactor,
            float G,
            float sameGroupBubbleAttractionForceLimit,
            float slimeBubbleAttractionForceLimit)
        {
            List<List<Vector2>> paths = new List<List<Vector2>>();

            float step = 0.002f;

            int currentColumn = 1;

            List<Vector2> currentSpeeds = new List<Vector2>();

            for (int i = 0; i < startPos.Count; i++)
            {
                currentSpeeds.Add(speedVector[i]);
            }

            for (int i = 0; i < startPos.Count; i++)
            {
                paths.Add(new List<Vector2>());
                paths[i].Add(startPos[i]);
            }

            for (float i = step; i < endTime; i += step)
            {
                List<Vector2> currentPositions = new List<Vector2>();

                for (int j = 0; j < paths.Count; j++)
                {
                    currentPositions.Add(paths[j][currentColumn - 1]);
                }

                List<Vector2> accelerationToBubbles = GravityController.GetAccelerationToBubblesParallel(
                    currentPositions,
                    bubblesMass,
                    sameColorFactor,
                    G,
                    sameGroupBubbleAttractionForceLimit);

                List<Vector2> accelerationToAttractors = GravityController.GetAccelerationToAttractorsParallel(
                    currentPositions,
                    attractorsPositions,
                    bubblesMass,
                    attractorsMasses,
                    attractorFactor,
                    G,
                    slimeBubbleAttractionForceLimit);

                for (int j = 0; j < startPos.Count; j++)
                {
                    Vector2 prevPos = paths[j][currentColumn - 1];

                    Vector2 acceleration = gravitation * gravityScales[j] + accelerationToBubbles[j] + accelerationToAttractors[j];

                    //Speed increased by acceleration each step
                    currentSpeeds[j] += acceleration * step;

                    currentSpeeds[j] *= (1 - linearDrags[j] * step);

                    Vector2 nextPosition = prevPos + (Vector2)(currentSpeeds[j]) * step;

                    paths[j].Add(nextPosition);
                }

                currentColumn++;
            }

            List<Vector2[]> result = new List<Vector2[]>();

            for (int i = 0; i < paths.Count; i++)
            {
                result.Add(paths[i].ToArray());
            }

            return result;
        }

        /// <summary>
        /// Calculates bubbles intersections. Result is stored in "Intersections" field.
        /// </summary>
        /// <param name="trajectories"></param>
        /// <param name="bubbleRadiuses"></param>
        /// <returns></returns>
        private List<Tuple<Vector2, List<BubbleConfig>, int>> CalculateIntersections(List<Tuple<BubbleConfig, Vector2[]>> trajectories, List<float> bubbleRadiuses, int intersectionsLimit = 20)
        {
            List<Tuple<Vector2, List<BubbleConfig>, int>> intersections = new List<Tuple<Vector2, List<BubbleConfig>, int>>();

            //Calculates all intersections and adds there info to cache.
            for (int column = 0; column < trajectories[0].Item2.Length; column++)
            {
                for (int k = 0; k < trajectories.Count; k++)
                {
                    float bubbleARadius = bubbleRadiuses[k];

                    for (int j = k + 1; j < trajectories.Count; j++)
                    {
                        float bubbleBRadius = bubbleRadiuses[j];

                        if ((trajectories[j].Item2[column] - trajectories[k].Item2[column]).magnitude < bubbleBRadius / 2 + bubbleARadius / 2)
                        {
                            List<BubbleConfig> intersectedBubbleConfigs = new List<BubbleConfig>();
                            intersectedBubbleConfigs.Add(trajectories[k].Item1);
                            intersectedBubbleConfigs.Add(trajectories[j].Item1);

                            Vector2 collisionPoint = trajectories[k].Item2[column] + (trajectories[j].Item2[column] - trajectories[k].Item2[column]).normalized * bubbleARadius / 2;

                            intersections.Add(new Tuple<Vector2, List<BubbleConfig>, int>(collisionPoint, intersectedBubbleConfigs, column));

                            if (intersections.Count > intersectionsLimit)
                            {
                                return intersections;
                            }
                        }
                    }

                }
            }

            return intersections;
        }


        /// <summary>
        /// Recalculates all bubbles trajectories.
        /// Calculations are done in separate thread.
        /// </summary>
        /// <param name="onCompleteCallback">callback on calculations complete </param>
        public void RecalculateTrajectories(Action onCompleteCallback)
        {
            //Debug.Log("Recalculating all trajectories");

            if (TrajectoryCalculationThread != null && TrajectoryCalculationThread.IsAlive)
            {
                TrajectoryCalculationThread.Abort();
            }

            var allCurrentBubbles = GameObject.FindObjectsOfType<BubbleConfig>();
            var allCurrentAttractors = GameObject.FindObjectsOfType<AttractorSlimeTrait>();

            EncounterSO currentEncounter = currentSelectedEncounter;

            if (currentEncounter == null)
            {
                if (!ShowedBubbleOutOfEncounterWarning)
                {
                    ShowedBubbleOutOfEncounterWarning = true;
                    Debug.LogWarning($"No trajectory can be drawn for bubbles that do not belong to any encounter. {SelectedBubbleConfig}");
                }

                CachedTrajectories = new Dictionary<BubbleConfig, Vector2[]>();

                Intersections = new List<Tuple<Vector2, List<BubbleConfig>, int>>();

                onCompleteCallback.Invoke();
                return;
            }
            else
            {
                ShowedBubbleOutOfEncounterWarning = false;
            }

            List<BubbleConfig> currentBubbles = (allCurrentBubbles.Where(bubble => currentEncounter.Zone.Contains((Vector2)bubble.transform.position))).ToList();

            List<AttractorSlimeTrait> currentAttractors = (allCurrentAttractors.Where(attractor => currentEncounter.Zone.Contains((Vector2)attractor.transform.position))).ToList();

            // Current bubbles grouped by their BubbleDefinition.attractionGroup.
            var bubblesByAttractionGroup = new Dictionary<int, List<BubbleConfig>>();

            List<Vector2> attractorsPositions = new List<Vector2>();
            List<float> attractorsMasses = new List<float>();

            for (int i = 0; i < currentAttractors.Count; i++)
            {
                attractorsPositions.Add(currentAttractors[i].transform.position);
                attractorsMasses.Add(currentAttractors[i].GetComponent<Slime>().Mass);
            }

            for (int i = 0; i < currentBubbles.Count; i++)
            {
                if (currentBubbles[i].bubbleDefinition?.editorAsset == null)
                {
                    continue;
                }

                BubbleDefinition bd = currentBubbles[i].bubbleDefinition.editorAsset;

                if (!bubblesByAttractionGroup.ContainsKey(bd.attractionGroup))
                {
                    bubblesByAttractionGroup.Add(bd.attractionGroup, new List<BubbleConfig>());
                }

                bubblesByAttractionGroup[bd.attractionGroup].Add(currentBubbles[i]);
            }

            int allBubbleTypesCount = ActualGameConfig.BubbleDefinitionReferences.Select(bd => bd.editorAsset.bubbleType).Distinct().Count();

            // TODO: is it possible that DefaultBubbleDefinition of GameConfigSO isnt included in its BubbleDefinitionRefereces?
            if (!ActualGameConfig.BubbleDefinitionReferences.Select(bd => bd.editorAsset.bubbleType)
                .Contains(ActualGameConfig.defaultBubbleDefinition.editorAsset.bubbleType))
            {
                allBubbleTypesCount++;
            }

            int activeBubbleTypesCount = bubblesByAttractionGroup.Count;

            float sameColorFactor = ActualGameConfig.SameGroupBubbleAttractionForce /
                                    GravityController.CalculateSameColorFactor(activeBubbleTypesCount, allBubbleTypesCount);
            float attractorFactor = GravityController.CalculateAttractorForceFactor1(currentBubbles.Count);

            //Debug.Log("Attractor factor = "+attractorFactor);


            if (currentBubbles.Count == 0 || bubblesByAttractionGroup.Count == 0) return;

            CachedTrajectories = new Dictionary<BubbleConfig, Vector2[]>();

            List<List<Vector2>> bubblesStartPositionsAll = new List<List<Vector2>>();
            List<List<Vector2>> bubblesSpeedVectorsAll = new List<List<Vector2>>();
            List<List<float>> bubblesMassesAll = new List<List<float>>();
            List<List<float>> bubblesGravityScalesAll = new List<List<float>>();
            List<List<float>> bubblesLinearDragsAll = new List<List<float>>();
            List<List<float>> bubblesRadiusesAll = new List<List<float>>();

            foreach (var bubbleGroup in bubblesByAttractionGroup)
            {
                List<Vector2> bubblesStartPositions = new List<Vector2>();
                List<Vector2> bubblesSpeedVectors = new List<Vector2>();
                List<float> bubblesMasses = new List<float>();
                List<float> bubblesGravityScales = new List<float>();
                List<float> bubblesLinearDrags = new List<float>();
                List<float> bubblesRadisuses = new List<float>();

                for (int i = 0; i < bubbleGroup.Value.Count; i++)
                {
                    var BubbleConfig = bubbleGroup.Value[i];

                    //Debug.Log($"Working with {BubbleConfig}");

                    bubblesStartPositions.Add(BubbleConfig.gameObject.transform.position);
                    bubblesSpeedVectors.Add(BubbleConfig.initialVelocity);

                    bubblesMasses.Add(BubbleConfig.bubbleDefinition.editorAsset.Mass);
                    bubblesGravityScales.Add(BubbleConfig.bubbleDefinition.editorAsset.GravityScale);
                    bubblesLinearDrags.Add(BubbleConfig.bubbleDefinition.editorAsset.LinearDrag);

                    float externalRadius = BubbleConfig.gameObject.GetComponent<Bubble>().Radius;

                    bubblesRadisuses.Add(externalRadius * 2);// ;// * 2);
                }

                bubblesStartPositionsAll.Add(bubblesStartPositions);
                bubblesSpeedVectorsAll.Add(bubblesSpeedVectors);
                bubblesMassesAll.Add(bubblesMasses);
                bubblesGravityScalesAll.Add(bubblesGravityScales);
                bubblesLinearDragsAll.Add(bubblesLinearDrags);
                bubblesRadiusesAll.Add(bubblesRadisuses);

            }

            float endTime = EditorTrajectoryLength;

            //All data needed for calculations got using Unity API before starting calculation thread. Now when all needed data is gathered we can start calculation thread.
            TrajectoryCalculationThread = new Thread(() => ThreadedRecalculateTrajectories(
                bubblesStartPositionsAll,
                bubblesSpeedVectorsAll,
                bubblesMassesAll,
                bubblesGravityScalesAll,
                bubblesLinearDragsAll,
                bubblesRadiusesAll,
                bubblesByAttractionGroup,
                attractorsPositions,
                attractorsMasses,
                sameColorFactor,
                attractorFactor,
                ActualGameConfig.G,
                ActualGameConfig.SameGroupBubbleAttractionForceLimit,
                ActualGameConfig.SlimeBubbleAttractionForceLimit,
                endTime,
                onCompleteCallback));

            TrajectoryCalculationThread.Start();

        }

        /// <summary>
        /// Recalculates all trajectories.
        /// Is used to work in separate thread.
        /// Result will be stored in "CachedTrajectories" and "Intersection" fields.
        /// </summary>
        /// <param name="bubblesStartPositionsAll">Stores lists of start positions for each bubble group</param>
        /// <param name="bubblesSpeedVectorsAll">Stores lists of speed vectors for each bubble group</param>
        /// <param name="bubblesMassesAll">Stores lists of masses for each bubble group</param>
        /// <param name="bubblesGravityScalesAll">Stores lists of gravity scales for each bubble group</param>
        /// <param name="bubblesLinearDragsAll">Stores lists of linear each bubble group</param>
        /// <param name="currentBubblesGroupedByType">Stores lists of bubbles for each bubble group</param>
        /// <param name="attractorsPositions">Stores start position for each attractor</param>
        /// <param name="attractorsMasses">Stores masses for each attractor</param>
        /// <param name="sameColorFactor">Same color gravity factor</param>
        /// <param name="attractorFactor">Attractor gravity factor</param>
        /// <param name="G"></param>
        /// <param name="endTime">Trajectory max time limit (min limit is always zero)</param>
        /// <param name="onCompleteCallback">Callback will be invoked when calculations are done</param>
    private void ThreadedRecalculateTrajectories(List<List<Vector2>> bubblesStartPositionsAll,
            List<List<Vector2>> bubblesSpeedVectorsAll,
            List<List<float>> bubblesMassesAll,
            List<List<float>> bubblesGravityScalesAll,
            List<List<float>> bubblesLinearDragsAll,
            List<List<float>> bubbleRadiusesAll,
            Dictionary<int, List<BubbleConfig>> currentBubblesGroupedByType,
            List<Vector2> attractorsPositions,
            List<float> attractorsMasses,
            float sameColorFactor,
            float attractorFactor,
            float G,
            float sameGroupBubbleAttractionForceLimit,
            float slimeBubbleAttractionForceLimit,
            float endTime,
            Action onCompleteCallback)
        {
            try
            {
                int currentGroupIndex = 0;

                //Stopwatch stopwatch = new Stopwatch();

                //stopwatch.Start();

                List<Tuple<BubbleConfig, Vector2[]>> newTrajectories = new List<Tuple<BubbleConfig, Vector2[]>>();

                List<float> bubbleRadiuses = new List<float>();

                foreach (var bubbleGroup in currentBubblesGroupedByType)
                {
                    List<Vector2[]> trajectories = CalculatePathsParallel(endTime,
                        bubblesStartPositionsAll[currentGroupIndex],
                        bubblesMassesAll[currentGroupIndex],
                        attractorsPositions,
                        attractorsMasses,
                        bubblesGravityScalesAll[currentGroupIndex],
                        bubblesLinearDragsAll[currentGroupIndex],
                        bubblesSpeedVectorsAll[currentGroupIndex],
                        Vector2.down * G,
                        attractorFactor,
                        sameColorFactor,
                        G,
                        sameGroupBubbleAttractionForceLimit,
                        slimeBubbleAttractionForceLimit);

                    for (int i = 0; i < trajectories.Count; i++)
                    {
                        var BubbleConfig = bubbleGroup.Value[i];

                        newTrajectories.Add(new Tuple<BubbleConfig, Vector2[]>(BubbleConfig, trajectories[i]));
                        bubbleRadiuses.Add(bubbleRadiusesAll[currentGroupIndex][i]);
                    }

                    currentGroupIndex++;
                }

                Intersections = CalculateIntersections(newTrajectories, bubbleRadiuses, 1);

                for (int i = 0; i < newTrajectories.Count; i++)
                {
                    CachedTrajectories.Add(newTrajectories[i].Item1, newTrajectories[i].Item2);
                }

                if (onCompleteCallback != null)
                {
                    mainThreadDispatcher.ExecuteInMainThread(onCompleteCallback);
                }

                //stopwatch.Stop();

                //Debug.Log("Time taken to calculate trajectories: "+stopwatch.ElapsedMilliseconds/1000f+"s");
            }
            catch (Exception exception)
            {
                // TODO: Thread abort cleanup should be done here. Now there is nothing to clean and this block is used only to check if thread was aborted (for debug)
                //ShowCalculationMessage = false;

                //Debug.LogError($"Calculation thread failed.\n{exception.Message}\n{exception.StackTrace}");
                //Debug.LogException(exception);
            }

        }

#endregion

    }
}

#endif
