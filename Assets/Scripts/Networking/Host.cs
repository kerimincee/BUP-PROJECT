using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Assertions;

// high level host networking script
public class Host : NetworkSystem
{
    public const int PlayerId = 0;
    UNetHost _host;
    MessageDispatcher _msgDispatcher;
    LevelManager _lvlManager;
    PlayerSystem _playerSys;
    AICarSyncSystem _aiCarSystem;
    AIPedestrianSyncSystem _aiPedestrianSyncSystem;
    WorldLogger _logger;
    WorldLogger _fixedTimeLogger;
    HMIManager _hmiManager;
    VisualSyncManager _visualSyncManager;

    List<int> _playerRoles = new List<int>();
    bool[] _playerReadyStatus = new bool[UNetConfig.MaxPlayers];
    float _lastPoseUpdateSent;
    int _selectedExperiment;
    const float PoseUpdateInterval = 0.01f;

    TrafficLightsSystem _lights;
    NetworkingManager.Trial currentTrial;

    public Host(LevelManager levelManager, PlayerSystem playerSys, AICarSyncSystem aiCarSystem, WorldLogger logger, WorldLogger fixedLogger, NetworkingManager.Trial instantStartParams)
    {
        currentTrial = instantStartParams;
        _playerSys = playerSys;
        _lvlManager = levelManager;
        _aiCarSystem = aiCarSystem;
        _logger = logger;
        _fixedTimeLogger = fixedLogger;
        _host = new UNetHost();
        _host.Init();

        _hmiManager = GameObject.FindObjectOfType<HMIManager>();
        Assert.IsNotNull(_hmiManager, "Missing HMI manager");
        _visualSyncManager = GameObject.FindObjectOfType<VisualSyncManager>();
        Assert.IsNotNull(_visualSyncManager, "Missing VS manager");

        _currentState = NetState.Lobby;
        _msgDispatcher = new MessageDispatcher();

        //set up message handlers
        _msgDispatcher.AddStaticHandler((int)MsgId.C_UpdatePose, OnClientPoseUpdate);
        _msgDispatcher.AddStaticHandler((int)MsgId.C_Ready, OnClientReady);
        _msgDispatcher.AddStaticHandler((int)MsgId.B_Ping, OnPing);
        _hmiManager.InitHost(_host, _msgDispatcher);
        aiCarSystem.InitHost(_host);
        for (int i = 0; i < UNetConfig.MaxPlayers; i++)
        {
            _playerRoles.Add(-1);
        }
    }

    public override void Shutdown()
    {
        _host.Shutdown();
        _currentState = NetState.Disconnected;
    }

    //handles ping message
    private void OnPing(ISynchronizer sync, int srcPlayerId)
    {
        var msg = NetMsg.Read<PingMsg>(sync);
        _host.SendUnreliableToPlayer(msg, srcPlayerId);
    }
    
    //handles client ready message
    void OnClientReady(ISynchronizer sync, int player)
    {
        NetMsg.Read<ReadyMsg>(sync);
        _playerReadyStatus[player] = true;
    }

    //applies pose updates received from other players
    void OnClientPoseUpdate(ISynchronizer sync, int player)
    {
        var msg = NetMsg.Read<UpdateClientPose>(sync);
        _playerSys.GetAvatar(player).ApplyPose(msg.Pose);
    }

    public void BroadcastMessage<T>(T msg) where T : INetMessage 
        => _host.BroadcastReliable(msg);

    public override void FixedUpdate()
    {
        if (_currentState == NetState.InGame)
        {
            _fixedTimeLogger.LogFrame(0, Time.fixedTime);
        }
    }

    public override void Update()
    {
        switch (_currentState)
        {
            case NetState.Disconnected:
                break;
            case NetState.Lobby:
                _host.Update(_msgDispatcher);
                switch (_transitionPhase)
                {
                    case TransitionPhase.None:
                        break;
                    case TransitionPhase.LoadingLevel:
                        if (!_lvlManager.Loading)
                        {
                            _transitionPhase = TransitionPhase.WaitingForAwakes;
                        }
                        break;
                    case TransitionPhase.WaitingForAwakes:
                        _playerReadyStatus[Host.PlayerId] = true;
                        if (AllReady())
                        {
                            _lights = GameObject.FindObjectOfType<TrafficLightsSystem>();
                            foreach (var carSpawner in _lvlManager.ActiveExperiment.CarSpawners)
                            {
                                carSpawner.Init(_aiCarSystem);
                            }
                            _aiPedestrianSyncSystem = _lvlManager.ActiveExperiment.AIPedestrians;
                            _aiPedestrianSyncSystem.InitHost(_host);
                            ExperimentRoleDefinition experimentRoleDefinition;
                            var role = _playerRoles[Host.PlayerId];
                            var roleName = "No role";
                            if (role != -1) {
                                experimentRoleDefinition = _lvlManager.ActiveExperiment.Roles[role];
                                if (experimentRoleDefinition.AutonomousPath != null) {
                                    _playerSys.ActivatePlayerAICar();
                                }
                                roleName = experimentRoleDefinition.Name;
                            }
                            _host.BroadcastReliable(new AllReadyMsg());
                            _transitionPhase = TransitionPhase.None;
                            _currentState = NetState.InGame;
                            Time.timeScale = 1f;
                            _logger.BeginLog($"HostLog-{roleName}-", _lvlManager.ActiveExperiment, _lights, Time.realtimeSinceStartup, true);
                            _fixedTimeLogger.BeginLog($"HostFixedTimeLog-{roleName}-", _lvlManager.ActiveExperiment, _lights, Time.fixedTime, false);
                        }
                        break;
                }
                break;
            case NetState.InGame:
                _host.Update(_msgDispatcher);
                UpdateGame();
                _logger.LogFrame(0, Time.realtimeSinceStartup);
                break;
        }
    }

    bool startSimulation = false;
    bool AllReady()
    {
        return startSimulation;
    }

    void ForEachConnectedPlayer(Action<int, Host> action)
    {
        for (int i = 1; i < UNetConfig.MaxPlayers; i++)
        {
            if (_host.PlayerConnected(i))
            {
                action(i, this);
            }
        }
    }

    //displays role selection GUI for a single player
    static void SelectRoleGUI(int player, Host host, ExperimentRoleDefinition[] roles, bool RunTrialSequenceAutomatically)
    {
        if (RunTrialSequenceAutomatically)
        {
            host._playerRoles[player] = host.currentTrial.roleIndex;
        }
        else 
        {
            string playerName = player == Host.PlayerId ? "Host" : $"Player {player}";
            GUILayout.Label(playerName == "Host" ? "Bu bilgisayar" : playerName, ProfessionalGUI.Muted);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < roles.Length; i++)
            {
                bool selected = host._playerRoles[player] == i;
                GUIStyle style = selected ? ProfessionalGUI.SelectedButton : ProfessionalGUI.Button;
                string roleName = roles[i].Name == "Driver" ? "Sürücü" : roles[i].Name;
                if (GUILayout.Button((selected ? "✓  " : "") + roleName, style))
                {
                    host._playerRoles[player] = i;
                }
            }
            GUILayout.EndHorizontal();
        }
    }

    //displays role selection GUI
    void PlayerRolesGUI(bool RunTrialSequenceAutomatically)
    {
        GUILayout.Label("ROL SEÇİMİ", ProfessionalGUI.Section);
        var roles = _lvlManager.Experiments[_selectedExperiment].Roles;
        SelectRoleGUI(Host.PlayerId, this, roles, RunTrialSequenceAutomatically);
        ForEachConnectedPlayer((player, host) => SelectRoleGUI(player, host, roles, RunTrialSequenceAutomatically));
    }

    bool started;
    //initializes experiment - sets it up locally and broadcasts experiment configuration message
    void PrepareSimulation()
    {
        if (started)
        {
            return;
        } else
        {
            started = true;
        }
        startSimulation = false;

        _msgDispatcher.ClearLevelMessageHandlers();
        _host.BroadcastReliable(new StartGameMsg
        {
            Roles = _playerRoles,
            Experiment = _selectedExperiment
        });
        for (int i = 0; i < UNetConfig.MaxPlayers; i++)
        {
            _playerReadyStatus[i] = false;
        }
        if (_playerRoles[Host.PlayerId] == -1)
        {
            _lvlManager.LoadLevelNoLocalPlayer(_selectedExperiment, _playerRoles, currentTrial);
        }
        else
        {
            _lvlManager.LoadLevelWithLocalPlayer(_selectedExperiment, 0, _playerRoles, currentTrial);
        }
        _transitionPhase = TransitionPhase.LoadingLevel;
        Time.timeScale = 0;
    }

    void UpdateGame()
    {
        if (Time.realtimeSinceStartup - _lastPoseUpdateSent > PoseUpdateInterval)
        {
            _host.BroadcastUnreliable(new UpdatePoses
            {
                Poses = _playerSys.GatherPoses()
            });
            _lastPoseUpdateSent = Time.realtimeSinceStartup;
        }
        _lights?.UpdateHost(_host);
        _aiCarSystem.UpdateHost();
        _aiPedestrianSyncSystem.UpdateHost();
    }

    bool failRoleCheck = false;
    bool[] _activeRoles = new bool[UNetConfig.MaxPlayers];
    void CheckRoleConnected(int player, Host host)
    {
        var role = _playerRoles[player];
        if (role < 0 || role >= _lvlManager.Experiments[_selectedExperiment].Roles.Length)
        {
            failRoleCheck = true;
            return;
        }
        if (_activeRoles[role])
        {
            failRoleCheck = true;
        }
        else
        {
            _activeRoles[role] = true;
        }
    }
    bool AllRolesSelected()
    {
        for (int i = 0; i < _activeRoles.Length; i++)
        {
            _activeRoles[i] = false;
        }
        failRoleCheck = false;
        CheckRoleConnected(Host.PlayerId, this);
        ForEachConnectedPlayer(CheckRoleConnected);
        return !failRoleCheck;
    }

    //displays host GUI
    public override void OnGUI(bool RunTrialSequenceAutomatically)
    {
        ProfessionalGUI.Ensure();
        if (!RunTrialSequenceAutomatically)
        {
            if (_currentState == NetState.Lobby)
            {
                float panelWidth = Mathf.Min(610f, Screen.width - 40f);
                float panelHeight = Mathf.Min(760f, Screen.height - 40f);
                GUILayout.BeginArea(new Rect(28f, 22f, panelWidth, panelHeight), ProfessionalGUI.Panel);
                GUILayout.Label("DENEY KONTROL MERKEZİ", ProfessionalGUI.Header);
                GUILayout.Label("Durum: Hazır  •  Bağlı katılımcı: " + _host.NumRemotePlayers, ProfessionalGUI.SubHeader);
            }
        } else
        {
            _selectedExperiment = currentTrial.experimentIndex;
        }
        switch (_currentState)
        {
            case NetState.Lobby:
            {
                    if (_transitionPhase == TransitionPhase.WaitingForAwakes)
                    {
                        string playerReadyStr = "Ready:\t";
                        string playerRolesStr = "Role :\t";
                        for (int i = 0; i < UNetConfig.MaxPlayers; i++)
                        {
                            playerReadyStr += (_playerReadyStatus[i]?"1":"0") + "\t";
                            playerRolesStr += _playerRoles[i] + "\t";
                        }
                        GUILayout.Label("SAHNE HAZIRLANIYOR", ProfessionalGUI.Section);
                        GUILayout.Label(playerReadyStr, ProfessionalGUI.Muted);
                        GUILayout.Label(playerRolesStr, ProfessionalGUI.Muted);
                        GUILayout.Label("Tüm bileşenler yüklendi. Simülasyonu başlatabilirsiniz.", ProfessionalGUI.Label);
                        if (GUILayout.Button("Simülasyonu Başlat", ProfessionalGUI.PrimaryButton) || RunTrialSequenceAutomatically)
                        {
                            if (!startSimulation) {
                                NetworkingManager.Instance.StartRecording();
                            }
                            startSimulation = true;
                        }
                    }
                    else
                    {
                        if (RunTrialSequenceAutomatically)
                        {
                            PrepareSimulation();
                            PlayerRolesGUI(RunTrialSequenceAutomatically);
                            _playerSys.SelectMode(currentTrial.InputMode);
                        }
                        else
                        {
                            GUILayout.Label("SENARYO SEÇİMİ", ProfessionalGUI.Section);
                            for (int i = 0; i < _lvlManager.Experiments.Length; i++)
                            {
                                bool selected = i == _selectedExperiment;
                                GUIStyle style = selected ? ProfessionalGUI.SelectedButton : ProfessionalGUI.Button;
                                if (GUILayout.Button((selected ? "✓  " : "") + _lvlManager.Experiments[i].Name, style))
                                {
                                    _selectedExperiment = i;
                                    for (int player = 0; player < _playerRoles.Count; player++)
                                        _playerRoles[player] = -1;
                                    if (_lvlManager.Experiments[i].Roles.Length == 1)
                                        _playerRoles[Host.PlayerId] = 0;
                                }
                            }
                            PlayerRolesGUI(RunTrialSequenceAutomatically);
                            GUILayout.Label("KONTROL MODU", ProfessionalGUI.Section);
                            _playerSys.SelectModeGUI();
                            GUILayout.Space(12f);
                            bool rolesReady = AllRolesSelected();
                            GUI.enabled = rolesReady;
                            if (GUILayout.Button(rolesReady ? "Deneyi Hazırla" : "Devam etmek için rol seçin", ProfessionalGUI.PrimaryButton))
                                PrepareSimulation();
                            GUI.enabled = true;
                        }
                    }
                break;
            }
            case NetState.InGame:
            {
                _hmiManager.DoHostGUI(this);
                _visualSyncManager.DoHostGUI(this);
            }
            break;
        }

        if (!RunTrialSequenceAutomatically && _currentState == NetState.Lobby)
            GUILayout.EndArea();
    }
}
