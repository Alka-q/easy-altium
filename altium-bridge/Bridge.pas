{==============================================================================}
{ EasyAltium (LCSC Loader) - Altium Designer DelphiScript Bridge               }
{ File    : Bridge.pas                                                         }
{ Project : LCSC_Bridge.PrjScr                                                 }
{ Purpose : Reads the current_part.ini IPC contract produced by the EasyAltium }
{           desktop host, installs/refreshes libraries (.SchLib, .PcbLib,      }
{           .DbLib) in Altium Designer, and if an active schematic (.SchDoc)   }
{           is open, instantiates the symbol with its footprint link and full  }
{           LCSC/JLCPCB BOM parameters onto the interactive cursor or sheet.   }
{           Also executes the MakeLibrary fallback workflow when requested.    }
{                                                                              }
{ CLI Trigger Command:                                                         }
{ "C:\Program Files\Altium\AD26\X2.EXE"                                        }
{   -RScriptingSystem:RunScript(ProjectName="<Path>\LCSC_Bridge.PrjScr"|       }
{   ProcName="Bridge.pas>PlaceOrLoadComponent")                                }
{==============================================================================}

{------------------------------------------------------------------------------}
{ Helper: Resolves the workspace root directory from LCSC_Bridge.PrjScr        }
{------------------------------------------------------------------------------}
function ResolveWorkspaceRootDir : String;
var
    Workspace : IWorkspace;
    Project   : IProject;
    ProjIndex : Integer;
    ProjPath  : String;
    ProjDir   : String;
begin
    Result := '';
    Workspace := GetWorkspace;
    if Workspace <> nil then
    begin
        for ProjIndex := 0 to Workspace.DM_ProjectCount - 1 do
        begin
            Project := Workspace.DM_Projects(ProjIndex);
            if Project <> nil then
            begin
                ProjPath := Project.DM_ProjectFullPath;
                if Pos('LCSC_BRIDGE.PRJSCR', UpperCase(ProjPath)) > 0 then
                begin
                    ProjDir := ExtractFilePath(ProjPath);
                    Result := ExpandFileName(ProjDir + '..\');
                    Exit;
                end;
            end;
        end;
    end;
end;

{------------------------------------------------------------------------------}
{ Helper: Resolves default log file path                                       }
{------------------------------------------------------------------------------}
function ResolveDefaultLogFilePath : String;
var
    WorkspaceDir : String;
    LocalAppData : String;
begin
    WorkspaceDir := ResolveWorkspaceRootDir;
    if WorkspaceDir <> '' then
    begin
        Result := WorkspaceDir + 'ipc\bridge_status.log';
        Exit;
    end;

    LocalAppData := GetEnvironmentVariable('LOCALAPPDATA');
    if LocalAppData <> '' then
    begin
        Result := LocalAppData + '\EasyAltium\ipc\bridge_status.log';
        Exit;
    end;

    Result := 'bridge_status.log';
end;

{------------------------------------------------------------------------------}
{ Helper: Appends a timestamped log line to the bridge log file                }
{------------------------------------------------------------------------------}
procedure WriteBridgeLog(const LogFilePath : String; const Level : String; const Msg : String);
var
    LogLines   : TStringList;
    TargetPath : String;
    LogLine    : String;
begin
    TargetPath := Trim(LogFilePath);
    if TargetPath = '' then
        TargetPath := ResolveDefaultLogFilePath;

    LogLines := TStringList.Create;
    try
        try
            if FileExists(TargetPath) then
                LogLines.LoadFromFile(TargetPath);

            LogLine := '[' + DateTimeToStr(Now) + '] [' + Level + '] ' + Msg;
            LogLines.Add(LogLine);
            LogLines.SaveToFile(TargetPath);
        except
            { Logging errors must not interrupt the main placement workflow }
        end;
    finally
        LogLines.Free;
    end;
end;

{------------------------------------------------------------------------------}
{ Helper: Resolves current_part.ini path from LCSC_Bridge.PrjScr or AppData    }
{------------------------------------------------------------------------------}
function ResolveIniFilePath : String;
var
    Workspace     : IWorkspace;
    Project       : IProject;
    ProjIndex     : Integer;
    ProjPath      : String;
    ProjDir       : String;
    CandidatePath : String;
    LocalAppData  : String;
begin
    Result := '';

    { 1. Check ..\ipc\current_part.ini relative to the open LCSC_Bridge.PrjScr }
    Workspace := GetWorkspace;
    if Workspace <> nil then
    begin
        for ProjIndex := 0 to Workspace.DM_ProjectCount - 1 do
        begin
            Project := Workspace.DM_Projects(ProjIndex);
            if Project <> nil then
            begin
                ProjPath := Project.DM_ProjectFullPath;
                if Pos('LCSC_BRIDGE.PRJSCR', UpperCase(ProjPath)) > 0 then
                begin
                    ProjDir := ExtractFilePath(ProjPath);
                    CandidatePath := ExpandFileName(ProjDir + '..\ipc\current_part.ini');
                    if FileExists(CandidatePath) then
                    begin
                        Result := CandidatePath;
                        Exit;
                    end;

                    CandidatePath := ProjDir + 'current_part.ini';
                    if FileExists(CandidatePath) then
                    begin
                        Result := CandidatePath;
                        Exit;
                    end;
                end;
            end;
        end;
    end;

    { 2. Check %LOCALAPPDATA%\EasyAltium\ipc\current_part.ini mirror }
    LocalAppData := GetEnvironmentVariable('LOCALAPPDATA');
    if LocalAppData <> '' then
    begin
        CandidatePath := LocalAppData + '\EasyAltium\ipc\current_part.ini';
        if FileExists(CandidatePath) then
        begin
            Result := CandidatePath;
            Exit;
        end;
    end;
end;

{------------------------------------------------------------------------------}
{ Helper: Updates the [Result] section in current_part.ini                     }
{------------------------------------------------------------------------------}
procedure UpdateIniResult(
    Ini            : TMemIniFile;
    const State    : String;
    const Action   : String;
    const Target   : String;
    const ErrorMsg : String
);
begin
    if Ini = nil then Exit;
    try
        Ini.WriteString('Result', 'State', State);
        Ini.WriteString('Result', 'ExecutedAction', Action);
        Ini.WriteString('Result', 'TargetDocument', Target);
        Ini.WriteString('Result', 'ErrorMessage', ErrorMsg);
        Ini.WriteString('Result', 'CompletedAt', DateTimeToStr(Now));
        Ini.UpdateFile;
    except
        { Ignore transient file lock if any }
    end;
end;

{------------------------------------------------------------------------------}
{ Helper: Checks if a library is already installed in Altium                   }
{------------------------------------------------------------------------------}
function IsLibraryAlreadyInstalled(const LibPath : String) : Boolean;
var
    IntLibMan : IIntegratedLibraryManager;
    Idx       : Integer;
    InstPath  : String;
begin
    Result := False;
    if Trim(LibPath) = '' then Exit;

    IntLibMan := IntegratedLibraryManager;
    if IntLibMan = nil then Exit;

    try
        for Idx := 0 to IntLibMan.InstalledLibraryCount - 1 do
        begin
            InstPath := IntLibMan.InstalledLibraryPath(Idx);
            if UpperCase(Trim(InstPath)) = UpperCase(Trim(LibPath)) then
            begin
                Result := True;
                Exit;
            end;
        end;
    except
        Result := False;
    end;
end;

{------------------------------------------------------------------------------}
{ Helper: Installs or refreshes a single library (.SchLib/.PcbLib/.DbLib)      }
{------------------------------------------------------------------------------}
procedure InstallOrRefreshSingleLibrary(const LibPath : String; const LogFilePath : String);
var
    IntLibMan : IIntegratedLibraryManager;
    OpenDoc   : IServerDocument;
begin
    if Trim(LibPath) = '' then Exit;
    if not FileExists(LibPath) then
    begin
        WriteBridgeLog(LogFilePath, 'WARN', 'Library file not found, skipping: ' + LibPath);
        Exit;
    end;

    IntLibMan := IntegratedLibraryManager;
    if IntLibMan = nil then
    begin
        WriteBridgeLog(LogFilePath, 'WARN', 'IntegratedLibraryManager is unavailable.');
        Exit;
    end;

    try
        if IsLibraryAlreadyInstalled(LibPath) then
        begin
            { Re-install to refresh Altium's internal library cache if updated on disk }
            try
                IntLibMan.UninstallLibrary(LibPath);
            except
            end;
            IntLibMan.InstallLibrary(LibPath);
            WriteBridgeLog(LogFilePath, 'INFO', 'Installed library refreshed: ' + LibPath);
        end
        else
        begin
            IntLibMan.InstallLibrary(LibPath);
            WriteBridgeLog(LogFilePath, 'INFO', 'Library installed into Altium: ' + LibPath);
        end;

        { Refresh open library document view if open in editor }
        if Client <> nil then
        begin
            if Client.IsDocumentOpen(LibPath) then
            begin
                OpenDoc := Client.GetDocumentByPath(LibPath);
                if OpenDoc <> nil then
                    OpenDoc.Modified := False;
            end;
        end;
    except
        WriteBridgeLog(LogFilePath, 'ERROR', 'Error while installing library: ' + LibPath);
    end;
end;

{------------------------------------------------------------------------------}
{ Helper: Installs all relevant libraries (.SchLib, .PcbLib, .DbLib)           }
{------------------------------------------------------------------------------}
procedure EnsureLibrariesInstalled(Ini : TMemIniFile; const LogFilePath : String);
var
    LibraryMode              : String;
    SchLibPath               : String;
    PcbLibPath               : String;
    DbLibPath                : String;
    UsePassiveTemplateSymbol : Integer;
    PassiveTemplateSchLib    : String;
begin
    LibraryMode              := Ini.ReadString('Transaction', 'LibraryMode', 'MasterLib');
    SchLibPath               := Ini.ReadString('LibraryPaths', 'SchLibPath', '');
    PcbLibPath               := Ini.ReadString('LibraryPaths', 'PcbLibPath', '');
    DbLibPath                := Ini.ReadString('LibraryPaths', 'DbLibPath', '');
    UsePassiveTemplateSymbol := Ini.ReadInteger('LibraryPaths', 'UsePassiveTemplateSymbol', 0);
    PassiveTemplateSchLib    := Ini.ReadString('LibraryPaths', 'PassiveTemplateSchLibPath', '');

    InstallOrRefreshSingleLibrary(PcbLibPath, LogFilePath);
    InstallOrRefreshSingleLibrary(SchLibPath, LogFilePath);

    if UsePassiveTemplateSymbol = 1 then
        InstallOrRefreshSingleLibrary(PassiveTemplateSchLib, LogFilePath);

    if UpperCase(Trim(LibraryMode)) = 'SQLITEDBLIB' then
        InstallOrRefreshSingleLibrary(DbLibPath, LogFilePath);
end;

{------------------------------------------------------------------------------}
{ Helper: Attaches or updates the PCB footprint implementation on a component  }
{------------------------------------------------------------------------------}
procedure AttachOrUpdateFootprint(
    Comp               : ISch_Component;
    const FootprintRef : String;
    const PcbLibPath   : String;
    const LogFilePath  : String
);
var
    ImplIterator : ISch_Iterator;
    SchImpl      : ISch_Implementation;
    FoundImpl    : Boolean;
begin
    if (Comp = nil) or (Trim(FootprintRef) = '') then Exit;

    FoundImpl := False;

    try
        ImplIterator := Comp.SchIterator_Create;
        ImplIterator.AddFilter_ObjectSet(MkSet(eImplementation));
        try
            SchImpl := ImplIterator.FirstSchObject;
            while SchImpl <> nil do
            begin
                if UpperCase(SchImpl.ModelType) = UpperCase(cDocKind_PcbLib) then
                begin
                    if UpperCase(SchImpl.ModelName) = UpperCase(FootprintRef) then
                    begin
                        SchImpl.IsCurrent := True;
                        FoundImpl := True;
                    end
                    else
                    begin
                        SchImpl.IsCurrent := False;
                    end;
                end;
                SchImpl := ImplIterator.NextSchObject;
            end;
        finally
            Comp.SchIterator_Destroy(ImplIterator);
        end;

        if not FoundImpl then
        begin
            SchImpl := Comp.AddSchImplementation;
            if SchImpl <> nil then
            begin
                SchImpl.ModelName := FootprintRef;
                SchImpl.ModelType := cDocKind_PcbLib;
                SchImpl.ClearAllDatafileLinks;
                if (Trim(PcbLibPath) <> '') and FileExists(PcbLibPath) then
                    SchImpl.AddDataFileLink(FootprintRef, PcbLibPath, cDocKind_PcbLib);
                SchImpl.IsCurrent := True;
                WriteBridgeLog(LogFilePath, 'INFO', 'Footprint linked: ' + FootprintRef);
            end;
        end;
    except
        WriteBridgeLog(LogFilePath, 'WARN', 'Warning while linking footprint: ' + FootprintRef);
    end;
end;

{------------------------------------------------------------------------------}
{ Helper: Inserts or updates a single schematic component parameter            }
{------------------------------------------------------------------------------}
procedure UpsertComponentParameter(
    Comp            : ISch_Component;
    const ParamName : String;
    const ParamVal  : String
);
var
    ParamIterator : ISch_Iterator;
    Param         : ISch_Parameter;
    Found         : Boolean;
begin
    if (Comp = nil) or (Trim(ParamName) = '') then Exit;

    Found := False;
    ParamIterator := Comp.SchIterator_Create;
    ParamIterator.AddFilter_ObjectSet(MkSet(eParameter));
    try
        Param := ParamIterator.FirstSchObject;
        while Param <> nil do
        begin
            if UpperCase(Trim(Param.Name)) = UpperCase(Trim(ParamName)) then
            begin
                Param.Text := ParamVal;
                Found := True;
                Break;
            end;
            Param := ParamIterator.NextSchObject;
        end;
    finally
        Comp.SchIterator_Destroy(ParamIterator);
    end;

    if not Found then
    begin
        Param := SchServer.SchObjectFactory(eParameter, eCreate_GlobalCopy);
        if Param <> nil then
        begin
            Param.Name     := ParamName;
            Param.Text     := ParamVal;
            Param.IsHidden := True;
            Param.Location := Comp.Location;
            Comp.AddSchObject(Param);
        end;
    end;
end;

{------------------------------------------------------------------------------}
{ Helper: Applies all BOM parameters from INI [Parameters] section             }
{------------------------------------------------------------------------------}
procedure ApplyComponentParameters(
    Comp              : ISch_Component;
    Ini               : TMemIniFile;
    const LogFilePath : String
);
var
    ParamKeys  : TStringList;
    Idx        : Integer;
    KeyName    : String;
    KeyVal     : String;
begin
    if (Comp = nil) or (Ini = nil) then Exit;

    ParamKeys := TStringList.Create;
    try
        Ini.ReadSection('Parameters', ParamKeys);
        for Idx := 0 to ParamKeys.Count - 1 do
        begin
            KeyName := Trim(ParamKeys.Strings[Idx]);
            if KeyName <> '' then
            begin
                KeyVal := Ini.ReadString('Parameters', KeyName, '');
                UpsertComponentParameter(Comp, KeyName, KeyVal);
            end;
        end;
        WriteBridgeLog(LogFilePath, 'INFO', IntToStr(ParamKeys.Count) + ' BOM parameters applied to component.');
    finally
        ParamKeys.Free;
    end;
end;

{------------------------------------------------------------------------------}
{ Helper: Clears current schematic selection                                   }
{------------------------------------------------------------------------------}
procedure ClearSchematicSelection(SchDoc : ISch_Document);
begin
    if SchDoc = nil then Exit;

    try
        SchDoc.DeselectAll;
    except
        try
            ResetParameters;
            AddStringParameter('Action', 'All');
            RunProcess('Sch:DeSelect');
        except
        end;
    end;
end;

{------------------------------------------------------------------------------}
{ Helper: Executes EasyEDA ASCII (.SchDoc/.PcbDoc) MakeLibrary fallback        }
{------------------------------------------------------------------------------}
function ExecuteFallbackMakeLibrary(Ini : TMemIniFile; const LogFilePath : String) : Boolean;
var
    FallbackSchDocPath : String;
    FallbackPcbDocPath : String;
    OpenedSchDoc       : IServerDocument;
    OpenedPcbDoc       : IServerDocument;
begin
    Result := False;
    FallbackSchDocPath := Ini.ReadString('LibraryPaths', 'FallbackSchDocPath', '');
    FallbackPcbDocPath := Ini.ReadString('LibraryPaths', 'FallbackPcbDocPath', '');

    WriteBridgeLog(LogFilePath, 'INFO', 'Starting Fallback MakeLibrary workflow...');

    if Client = nil then
    begin
        WriteBridgeLog(LogFilePath, 'ERROR', 'Altium Client object is unavailable.');
        Exit;
    end;

    try
        if (FallbackSchDocPath <> '') and FileExists(FallbackSchDocPath) then
        begin
            OpenedSchDoc := Client.OpenDocument('Sch', FallbackSchDocPath);
            if OpenedSchDoc <> nil then
            begin
                Client.ShowDocument(OpenedSchDoc);
                ResetParameters;
                RunProcess('Sch:MakeLibrary');
                WriteBridgeLog(LogFilePath, 'INFO', 'Sch:MakeLibrary triggered: ' + FallbackSchDocPath);
                Result := True;
            end;
        end;

        if (FallbackPcbDocPath <> '') and FileExists(FallbackPcbDocPath) then
        begin
            OpenedPcbDoc := Client.OpenDocument('Pcb', FallbackPcbDocPath);
            if OpenedPcbDoc <> nil then
            begin
                Client.ShowDocument(OpenedPcbDoc);
                ResetParameters;
                RunProcess('PCB:MakeLibrary');
                WriteBridgeLog(LogFilePath, 'INFO', 'PCB:MakeLibrary triggered: ' + FallbackPcbDocPath);
                Result := True;
            end;
        end;
    except
        WriteBridgeLog(LogFilePath, 'ERROR', 'Exception occurred during Fallback MakeLibrary.');
        Result := False;
    end;
end;

{------------------------------------------------------------------------------}
{ Helper: Loads and places the component onto the active schematic (.SchDoc)   }
{------------------------------------------------------------------------------}
function PlaceComponentOnSchematic(
    SchDoc            : ISch_Document;
    Ini               : TMemIniFile;
    const LogFilePath : String
) : Boolean;
var
    UsePassiveTemplate : Integer;
    EffectiveSchLib    : String;
    EffectiveLibRef    : String;
    PrimarySchLib      : String;
    PrimaryLibRef      : String;
    FootprintRef       : String;
    PcbLibPath         : String;
    DesignatorPrefix   : String;
    CommentText        : String;
    DescriptionText    : String;
    PlacementMethod    : String;
    TargetX_Mils       : Integer;
    TargetY_Mils       : Integer;
    Comp               : ISch_Component;
    IntLibMan          : IIntegratedLibraryManager;
begin
    Result := False;
    if (SchDoc = nil) or (SchServer = nil) then Exit;

    PrimarySchLib      := Ini.ReadString('LibraryPaths', 'SchLibPath', '');
    PrimaryLibRef      := Ini.ReadString('Component', 'LibRef', '');
    UsePassiveTemplate := Ini.ReadInteger('LibraryPaths', 'UsePassiveTemplateSymbol', 0);

    if UsePassiveTemplate = 1 then
    begin
        EffectiveSchLib := Ini.ReadString('LibraryPaths', 'PassiveTemplateSchLibPath', PrimarySchLib);
        EffectiveLibRef := Ini.ReadString('LibraryPaths', 'PassiveTemplateLibRef', PrimaryLibRef);
        if (Trim(EffectiveSchLib) = '') or (not FileExists(EffectiveSchLib)) then
        begin
            WriteBridgeLog(LogFilePath, 'WARN', 'Passive template library not found; falling back to LCSC symbol.');
            EffectiveSchLib := PrimarySchLib;
            EffectiveLibRef := PrimaryLibRef;
        end;
    end
    else
    begin
        EffectiveSchLib := PrimarySchLib;
        EffectiveLibRef := PrimaryLibRef;
    end;

    FootprintRef     := Ini.ReadString('Component', 'FootprintRef', '');
    PcbLibPath       := Ini.ReadString('LibraryPaths', 'PcbLibPath', '');
    DesignatorPrefix := Ini.ReadString('Component', 'DesignatorPrefix', 'U?');
    CommentText      := Ini.ReadString('Component', 'Comment', EffectiveLibRef);
    DescriptionText  := Ini.ReadString('Component', 'Description', '');
    PlacementMethod  := Ini.ReadString('Transaction', 'PlacementMethod', 'InteractiveCursor');
    TargetX_Mils     := Ini.ReadInteger('Transaction', 'TargetX_Mils', 2000);
    TargetY_Mils     := Ini.ReadInteger('Transaction', 'TargetY_Mils', 2000);

    WriteBridgeLog(
        LogFilePath,
        'INFO',
        'Loading symbol -> LibRef: ' + EffectiveLibRef + ' | Library: ' + EffectiveSchLib
    );

    { 1. Instantiate component via SchServer.LoadComponentFromLibrary }
    Comp := nil;
    try
        if FileExists(EffectiveSchLib) and (Trim(EffectiveLibRef) <> '') then
            Comp := SchServer.LoadComponentFromLibrary(EffectiveLibRef, EffectiveSchLib);
    except
        Comp := nil;
    end;

    { 2. If LoadComponentFromLibrary returns nil, try IntegratedLibraryManager.PlaceLibraryComponent }
    if Comp = nil then
    begin
        WriteBridgeLog(
            LogFilePath,
            'WARN',
            'SchServer.LoadComponentFromLibrary returned nil; trying IntegratedLibraryManager.PlaceLibraryComponent.'
        );
        IntLibMan := IntegratedLibraryManager;
        if (IntLibMan <> nil) and FileExists(EffectiveSchLib) then
        begin
            try
                IntLibMan.PlaceLibraryComponent(EffectiveLibRef, EffectiveSchLib, '');
                Result := True;
                Exit;
            except
                WriteBridgeLog(LogFilePath, 'ERROR', 'PlaceLibraryComponent call failed.');
                Exit;
            end;
        end;
        Exit;
    end;

    { 3. Begin Undo/Redo stack and schematic container registration }
    SchServer.ProcessControl.PreProcess(SchDoc, '');
    try
        ClearSchematicSelection(SchDoc);

        { Configure Designator, Comment, and Description }
        if Comp.Designator <> nil then
        begin
            Comp.Designator.Text     := DesignatorPrefix;
            Comp.Designator.IsHidden := False;
        end;

        if Comp.Comment <> nil then
        begin
            Comp.Comment.Text     := CommentText;
            Comp.Comment.IsHidden := False;
        end;

        if Trim(DescriptionText) <> '' then
            Comp.ComponentDescription := DescriptionText;

        Comp.CurrentPartID := 1;
        Comp.Location      := Point(MilsToCoord(TargetX_Mils), MilsToCoord(TargetY_Mils));

        { Attach Footprint and BOM Parameters }
        AttachOrUpdateFootprint(Comp, FootprintRef, PcbLibPath, LogFilePath);
        ApplyComponentParameters(Comp, Ini, LogFilePath);

        { Register component in active schematic document }
        SchDoc.RegisterSchObjectInContainer(Comp);
        SchServer.RobotManager.SendMessage(
            SchDoc.I_ObjectAddress,
            c_BroadCast,
            SCHM_PrimitiveRegistration,
            Comp.I_ObjectAddress
        );

        Comp.SetState_Selection(True);
        Comp.GraphicallyInvalidate;
    finally
        SchServer.ProcessControl.PostProcess(SchDoc, '');
    end;

    SchDoc.GraphicallyInvalidate;

    { 4. If InteractiveCursor mode is enabled, attach component to user cursor }
    if UpperCase(Trim(PlacementMethod)) = 'INTERACTIVECURSOR' then
    begin
        try
            ResetParameters;
            RunProcess('Sch:MoveSelectedObjects');
            WriteBridgeLog(LogFilePath, 'INFO', 'Component attached to cursor (Sch:MoveSelectedObjects).');
        except
            WriteBridgeLog(LogFilePath, 'WARN', 'Sch:MoveSelectedObjects could not start; component placed on sheet.');
        end;
    end;

    Result := True;
end;

{==============================================================================}
{ MAIN CLI ENTRY POINT: PlaceOrLoadComponent                                   }
{ Invoked by EasyAltium Host via X2.EXE -RScriptingSystem:RunScript.           }
{==============================================================================}
procedure PlaceOrLoadComponent;
var
    IniPath               : String;
    LogFilePath           : String;
    Ini                   : TMemIniFile;
    Action                : String;
    LcscId                : String;
    LibRef                : String;
    SchLibPath            : String;
    OpenLibIfNoSchDoc     : Integer;
    SilentMode            : Integer;
    CurrentSchDoc         : ISch_Document;
    OpenedLibDoc          : IServerDocument;
    PlacedOk              : Boolean;
    FallbackOk            : Boolean;
begin
    IniPath := ResolveIniFilePath;
    if (IniPath = '') or (not FileExists(IniPath)) then
    begin
        WriteBridgeLog(ResolveDefaultLogFilePath, 'ERROR', 'current_part.ini not found.');
        ShowMessage('EasyAltium Bridge Error: current_part.ini file not found!');
        Exit;
    end;

    Ini := TMemIniFile.Create(IniPath);
    try
        LogFilePath       := Ini.ReadString('Transaction', 'LogFilePath', ResolveDefaultLogFilePath);
        Action            := Ini.ReadString('Transaction', 'Action', 'PlaceOrLoad');
        LcscId            := Ini.ReadString('Component', 'LcscId', '');
        LibRef            := Ini.ReadString('Component', 'LibRef', '');
        SchLibPath        := Ini.ReadString('LibraryPaths', 'SchLibPath', '');
        OpenLibIfNoSchDoc := Ini.ReadInteger('Transaction', 'OpenLibraryIfNoSchDoc', 1);
        SilentMode        := Ini.ReadInteger('Transaction', 'SilentMode', 1);

        WriteBridgeLog(
            LogFilePath,
            'INFO',
            '=== PlaceOrLoadComponent Started | LCSC: ' + LcscId + ' | LibRef: ' + LibRef + ' | Action: ' + Action + ' ==='
        );

        UpdateIniResult(Ini, 'RUNNING', Action, 'None', '');

        { 1. Check Fallback MakeLibrary Workflow }
        if UpperCase(Trim(Action)) = 'FALLBACKMAKELIBRARY' then
        begin
            FallbackOk := ExecuteFallbackMakeLibrary(Ini, LogFilePath);
            if FallbackOk then
                UpdateIniResult(Ini, 'SUCCESS', 'FallbackMakeLibrary', Ini.ReadString('LibraryPaths', 'FallbackSchDocPath', 'MakeLibrary'), '')
            else
                UpdateIniResult(Ini, 'ERROR', 'FallbackMakeLibrary', 'None', 'MakeLibrary failed.');
            Exit;
        end;

        { 2. Install / Refresh .SchLib, .PcbLib, and .DbLib in Altium }
        EnsureLibrariesInstalled(Ini, LogFilePath);

        { Complete here if only library loading was requested }
        if UpperCase(Trim(Action)) = 'LOADONLY' then
        begin
            UpdateIniResult(Ini, 'SUCCESS', 'LoadOnly', SchLibPath, '');
            WriteBridgeLog(LogFilePath, 'INFO', 'Library load (LoadOnly) completed successfully.');
            if SilentMode = 0 then
                ShowMessage('EasyAltium: ' + LcscId + ' (' + LibRef + ') added to library.');
            Exit;
        end;

        { 3. Check Active Schematic (.SchDoc) and Place Component }
        CurrentSchDoc := nil;
        if SchServer <> nil then
            CurrentSchDoc := SchServer.GetCurrentSchDocument;

        if CurrentSchDoc <> nil then
        begin
            PlacedOk := PlaceComponentOnSchematic(CurrentSchDoc, Ini, LogFilePath);
            if PlacedOk then
            begin
                UpdateIniResult(Ini, 'SUCCESS', 'PlacedOnSchematic', CurrentSchDoc.DocumentName, '');
                WriteBridgeLog(
                    LogFilePath,
                    'INFO',
                    'Component placed on active schematic: ' + CurrentSchDoc.DocumentName
                );
            end
            else
            begin
                UpdateIniResult(
                    Ini,
                    'ERROR',
                    'PlaceFailed',
                    CurrentSchDoc.DocumentName,
                    'Could not load symbol from library: ' + LibRef
                );
                WriteBridgeLog(LogFilePath, 'ERROR', 'Could not load symbol from library: ' + LibRef);
                if SilentMode = 0 then
                    ShowMessage('EasyAltium: Could not load symbol from library (' + LibRef + ').');
            end;
        end
        else
        begin
            { No active .SchDoc is currently open }
            WriteBridgeLog(LogFilePath, 'INFO', 'No active .SchDoc found. Library installed.');
            if (OpenLibIfNoSchDoc = 1) and FileExists(SchLibPath) and (Client <> nil) then
            begin
                OpenedLibDoc := Client.OpenDocument('SchLib', SchLibPath);
                if OpenedLibDoc <> nil then
                    Client.ShowDocument(OpenedLibDoc);
                UpdateIniResult(Ini, 'SUCCESS', 'OpenedSchLib', SchLibPath, '');
            end
            else
            begin
                UpdateIniResult(Ini, 'SUCCESS', 'LibraryInstalledNoActiveSchDoc', SchLibPath, '');
                if SilentMode = 0 then
                    ShowMessage(
                        'EasyAltium: ' + LcscId + ' (' + LibRef + ') installed into library. ' +
                        'Open a schematic (.SchDoc) to place it.'
                    );
            end;
        end;
    finally
        Ini.Free;
    end;
end;

{==============================================================================}
{ HELPER ENTRY POINT: RunFallbackMakeLibrary                                   }
{ Alternative entry point to invoke MakeLibrary fallback directly via CLI      }
{==============================================================================}
procedure RunFallbackMakeLibrary;
var
    IniPath     : String;
    LogFilePath : String;
    Ini         : TMemIniFile;
begin
    IniPath := ResolveIniFilePath;
    if (IniPath = '') or (not FileExists(IniPath)) then Exit;

    Ini := TMemIniFile.Create(IniPath);
    try
        LogFilePath := Ini.ReadString('Transaction', 'LogFilePath', ResolveDefaultLogFilePath);
        if ExecuteFallbackMakeLibrary(Ini, LogFilePath) then
            UpdateIniResult(Ini, 'SUCCESS', 'FallbackMakeLibrary', 'MakeLibrary', '')
        else
            UpdateIniResult(Ini, 'ERROR', 'FallbackMakeLibrary', 'MakeLibrary', 'MakeLibrary error');
    finally
        Ini.Free;
    end;
end;

{==============================================================================}
{ TOOLBAR ENTRY POINT: LaunchOrShowEasyAltium                                  }
{ Launches or brings forward the EasyAltium desktop host when the toolbar      }
{ icon is clicked inside Altium Designer.                                      }
{==============================================================================}
procedure LaunchOrShowEasyAltium;
var
    IniPath      : String;
    WorkspaceDir : String;
    HostExePath  : String;
begin
    HostExePath := '';
    WorkspaceDir := ResolveWorkspaceRootDir;

    if WorkspaceDir = '' then
    begin
        IniPath := ResolveIniFilePath;
        if IniPath <> '' then
            WorkspaceDir := ExpandFileName(ExtractFilePath(IniPath) + '..\');
    end;

    if WorkspaceDir <> '' then
    begin
        HostExePath := WorkspaceDir + 'src\EasyAltium.Host\bin\Release\net8.0-windows\EasyAltium.exe';
        if not FileExists(HostExePath) then
            HostExePath := WorkspaceDir + 'src\EasyAltium.Host\bin\Debug\net8.0-windows\EasyAltium.exe';
        if not FileExists(HostExePath) then
            HostExePath := WorkspaceDir + 'EasyAltium.exe';
    end;

    if (HostExePath <> '') and FileExists(HostExePath) then
    begin
        RunApplication('"' + HostExePath + '" --show');
    end
    else
    begin
        ShowMessage('EasyAltium desktop client executable not found: ' + HostExePath);
    end;
end;
