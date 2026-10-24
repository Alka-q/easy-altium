{==============================================================================}
{ EasyAltium (LCSC Loader) - Altium Designer 26.10.1 DelphiScript Köprüsü      }
{ Dosya   : Bridge.pas                                                         }
{ Proje   : LCSC_Bridge.PrjScr                                                 }
{ Amaç    : C# İstemci Uygulamasının ürettiği current_part.ini sözleşmesini    }
{           okuyarak kütüphaneleri (.SchLib, .PcbLib, .DbLib) Altium'a yükler, }
{           aktif şematik (.SchDoc) açıksa sembolü, footprint bağlantısını ve  }
{           tüm LCSC/JLCPCB BOM parametrelerini oluşturup imlece/şematiğe      }
{           yerleştirir. Ayrıca karmaşık parçalar için MakeLibrary yedek       }
{           (Fallback) akışını yürütür.                                        }
{                                                                              }
{ CLI Tetikleme Komutu:                                                        }
{ "C:\Program Files\Altium\AD26\X2.EXE"                                        }
{   -RScriptingSystem:RunScript(ProjectName="<Yol>\LCSC_Bridge.PrjScr"|        }
{   ProcName="Bridge.pas>PlaceOrLoadComponent")                                }
{==============================================================================}

const
    DEFAULT_WORKSPACE_INI = 'D:\wb\antigravity\easyaltium-q\ipc\current_part.ini';
    DEFAULT_LOG_FILE      = 'D:\wb\antigravity\easyaltium-q\ipc\bridge_status.log';

{------------------------------------------------------------------------------}
{ Yardımcı Fonksiyon: Dosyaya Zaman Damgalı Log Yazar                          }
{------------------------------------------------------------------------------}
procedure WriteBridgeLog(const LogFilePath : String; const Level : String; const Msg : String);
var
    LogLines   : TStringList;
    TargetPath : String;
    LogLine    : String;
begin
    TargetPath := Trim(LogFilePath);
    if TargetPath = '' then
        TargetPath := DEFAULT_LOG_FILE;

    LogLines := TStringList.Create;
    try
        try
            if FileExists(TargetPath) then
                LogLines.LoadFromFile(TargetPath);

            LogLine := '[' + DateTimeToStr(Now) + '] [' + Level + '] ' + Msg;
            LogLines.Add(LogLine);
            LogLines.SaveToFile(TargetPath);
        except
            { Log yazma hatası ana akışı kesmemelidir }
        end;
    finally
        LogLines.Free;
    end;
end;

{------------------------------------------------------------------------------}
{ Yardımcı Fonksiyon: LCSC_Bridge.PrjScr Konumundan IPC INI Yolunu Çözümler    }
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

    { 1. Açık olan LCSC_Bridge.PrjScr projesinin dizinine göre ..\ipc\current_part.ini kontrolü }
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

    { 2. %LOCALAPPDATA%\EasyAltium\ipc\current_part.ini kontrolü }
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

    { 3. Varsayılan çalışma alanı yolu }
    if FileExists(DEFAULT_WORKSPACE_INI) then
    begin
        Result := DEFAULT_WORKSPACE_INI;
        Exit;
    end;
end;

{------------------------------------------------------------------------------}
{ Yardımcı Fonksiyon: current_part.ini [Result] Bölümünü Günceller             }
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
        { INI geri yazma kilidi varsa yut }
    end;
end;

{------------------------------------------------------------------------------}
{ Yardımcı Fonksiyon: Kütüphane Altium'da Yüklü mü Kontrol Eder                }
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
{ Yardımcı Fonksiyon: Tekil Kütüphaneyi (.SchLib/.PcbLib/.DbLib) Yükler/Yeniler}
{------------------------------------------------------------------------------}
procedure InstallOrRefreshSingleLibrary(const LibPath : String; const LogFilePath : String);
var
    IntLibMan : IIntegratedLibraryManager;
    OpenDoc   : IServerDocument;
begin
    if Trim(LibPath) = '' then Exit;
    if not FileExists(LibPath) then
    begin
        WriteBridgeLog(LogFilePath, 'WARN', 'Kütüphane dosyası bulunamadı, atlanıyor: ' + LibPath);
        Exit;
    end;

    IntLibMan := IntegratedLibraryManager;
    if IntLibMan = nil then
    begin
        WriteBridgeLog(LogFilePath, 'WARN', 'IntegratedLibraryManager erişilemedi.');
        Exit;
    end;

    try
        if IsLibraryAlreadyInstalled(LibPath) then
        begin
            { Kütüphane diskte güncellendiyse kaldırıp tekrar ekleyerek önbelleği tazele }
            try
                IntLibMan.UninstallLibrary(LibPath);
            except
            end;
            IntLibMan.InstallLibrary(LibPath);
            WriteBridgeLog(LogFilePath, 'INFO', 'Yüklü kütüphane yenilendi: ' + LibPath);
        end
        else
        begin
            IntLibMan.InstallLibrary(LibPath);
            WriteBridgeLog(LogFilePath, 'INFO', 'Kütüphane Altium''a eklendi: ' + LibPath);
        end;

        { Eğer kütüphane dökümanı Altium editöründe açıksa görünümü tazele }
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
        WriteBridgeLog(LogFilePath, 'ERROR', 'Kütüphane yüklenirken hata oluştu: ' + LibPath);
    end;
end;

{------------------------------------------------------------------------------}
{ Yardımcı Fonksiyon: Tüm İlgili Kütüphaneleri (.SchLib, .PcbLib, .DbLib) Kurar}
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
{ Yardımcı Fonksiyon: Bileşene Footprint (PCBLIB) Bağlantısını Ekler/Günceller }
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
            SchImpl := Comp.AddCurrentFootprint;
            if SchImpl <> nil then
            begin
                SchImpl.ModelName := FootprintRef;
                SchImpl.ModelType := cDocKind_PcbLib;
                SchImpl.ClearAllDataFileLinks;
                if (Trim(PcbLibPath) <> '') and FileExists(PcbLibPath) then
                    SchImpl.AddDataFileLink(FootprintRef, PcbLibPath, cDocKind_PcbLib);
                SchImpl.IsCurrent := True;
                WriteBridgeLog(LogFilePath, 'INFO', 'Footprint bağlandı: ' + FootprintRef);
            end;
        end;
    except
        WriteBridgeLog(LogFilePath, 'WARN', 'Footprint eşleştirilirken uyarı oluştu: ' + FootprintRef);
    end;
end;

{------------------------------------------------------------------------------}
{ Yardımcı Fonksiyon: Tek Bir Şematik Parametresini Ekler veya Günceller       }
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
{ Yardımcı Fonksiyon: INI [Parameters] Bölümündeki Tüm BOM Alanlarını Yükler   }
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
        WriteBridgeLog(LogFilePath, 'INFO', IntToStr(ParamKeys.Count) + ' adet BOM parametresi bileşene işlendi.');
    finally
        ParamKeys.Free;
    end;
end;

{------------------------------------------------------------------------------}
{ Yardımcı Fonksiyon: Seçimi Temizler (Yeni Bileşeni İmlece Almak İçin)        }
{------------------------------------------------------------------------------}
procedure ClearSchematicSelection(SchDoc : ISch_Document);
var
    Iterator : ISch_Iterator;
    Obj      : ISch_GraphicalObject;
begin
    if SchDoc = nil then Exit;

    Iterator := SchDoc.SchIterator_Create;
    if Iterator = nil then Exit;
    try
        Obj := Iterator.FirstSchObject;
        while Obj <> nil do
        begin
            if Obj.Selection then
                Obj.Selection := False;
            Obj := Iterator.NextSchObject;
        end;
    finally
        SchDoc.SchIterator_Destroy(Iterator);
    end;
end;

{------------------------------------------------------------------------------}
{ Yardımcı Fonksiyon: EasyEDA ASCII (.SchDoc/.PcbDoc) MakeLibrary Yedek Akışı  }
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

    WriteBridgeLog(LogFilePath, 'INFO', 'Fallback MakeLibrary akışı başlatılıyor...');

    if Client = nil then
    begin
        WriteBridgeLog(LogFilePath, 'ERROR', 'Altium Client nesnesi erişilemedi.');
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
                WriteBridgeLog(LogFilePath, 'INFO', 'Sch:MakeLibrary tetiklendi: ' + FallbackSchDocPath);
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
                WriteBridgeLog(LogFilePath, 'INFO', 'PCB:MakeLibrary tetiklendi: ' + FallbackPcbDocPath);
                Result := True;
            end;
        end;
    except
        WriteBridgeLog(LogFilePath, 'ERROR', 'Fallback MakeLibrary sırasında istisna oluştu.');
        Result := False;
    end;
end;

{------------------------------------------------------------------------------}
{ Yardımcı Fonksiyon: Bileşeni Aktif Şematiğe (.SchDoc) Yükler ve Yerleştirir  }
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
            WriteBridgeLog(LogFilePath, 'WARN', 'Pasif şablon kütüphanesi bulunamadı, LCSC sembolüne dönülüyor.');
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
        'Sembol yükleniyor -> LibRef: ' + EffectiveLibRef + ' | Kütüphane: ' + EffectiveSchLib
    );

    { 1. SchServer.LoadComponentFromLibrary ile bileşen örneğini oluştur }
    Comp := nil;
    try
        if FileExists(EffectiveSchLib) and (Trim(EffectiveLibRef) <> '') then
            Comp := SchServer.LoadComponentFromLibrary(EffectiveLibRef, EffectiveSchLib);
    except
        Comp := nil;
    end;

    { 2. Eğer LoadComponentFromLibrary nil dönerse IntegratedLibraryManager.PlaceLibraryComponent yedeğini dene }
    if Comp = nil then
    begin
        WriteBridgeLog(
            LogFilePath,
            'WARN',
            'SchServer.LoadComponentFromLibrary nil döndü, IntegratedLibraryManager.PlaceLibraryComponent deneniyor.'
        );
        IntLibMan := IntegratedLibraryManager;
        if (IntLibMan <> nil) and FileExists(EffectiveSchLib) then
        begin
            try
                IntLibMan.PlaceLibraryComponent(EffectiveLibRef, EffectiveSchLib, '');
                Result := True;
                Exit;
            except
                WriteBridgeLog(LogFilePath, 'ERROR', 'PlaceLibraryComponent çağrısı başarısız oldu.');
                Exit;
            end;
        end;
        Exit;
    end;

    { 3. Undo/Redo Yığını ve Şematik Konteyner İşlemlerini Başlat }
    SchServer.ProcessControl.PreProcess(SchDoc, '');
    try
        ClearSchematicSelection(SchDoc);

        { Designator, Comment ve Description ayarları }
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

        { Footprint ve BOM Parametrelerini İşle }
        AttachOrUpdateFootprint(Comp, FootprintRef, PcbLibPath, LogFilePath);
        ApplyComponentParameters(Comp, Ini, LogFilePath);

        { Bileşeni Aktif Şematiğe Kaydet }
        SchDoc.RegisterSchObjectInContainer(Comp);
        SchServer.RobotManager.SendMessage(
            SchDoc.I_ObjectAddress,
            c_BroadCast,
            SCHM_PrimitiveRegistration,
            Comp.I_ObjectAddress
        );

        Comp.Selection := True;
        Comp.GraphicallyInvalidate;
    finally
        SchServer.ProcessControl.PostProcess(SchDoc, '');
    end;

    SchDoc.GraphicallyInvalidate;

    { 4. InteractiveCursor modundaysa bileşeni fare imlecinin ucuna taşıma modunda ver }
    if UpperCase(Trim(PlacementMethod)) = 'INTERACTIVECURSOR' then
    begin
        try
            ResetParameters;
            RunProcess('Sch:MoveSelection');
            WriteBridgeLog(LogFilePath, 'INFO', 'Bileşen fare imlecine bağlandı (Sch:MoveSelection).');
        except
            WriteBridgeLog(LogFilePath, 'WARN', 'Sch:MoveSelection başlatılamadı, bileşen şematiğe eklendi.');
        end;
    end;

    Result := True;
end;

{==============================================================================}
{ ANA GİRİŞ NOKTASI (CLI Entry Point): PlaceOrLoadComponent                    }
{ C# Host tarafından X2.EXE -RScriptingSystem:RunScript ile çağrılır.          }
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
        WriteBridgeLog(DEFAULT_LOG_FILE, 'ERROR', 'current_part.ini bulunamadı.');
        ShowMessage('EasyAltium Köprü Hatası: current_part.ini dosyası bulunamadı!');
        Exit;
    end;

    Ini := TMemIniFile.Create(IniPath);
    try
        LogFilePath       := Ini.ReadString('Transaction', 'LogFilePath', DEFAULT_LOG_FILE);
        Action            := Ini.ReadString('Transaction', 'Action', 'PlaceOrLoad');
        LcscId            := Ini.ReadString('Component', 'LcscId', '');
        LibRef            := Ini.ReadString('Component', 'LibRef', '');
        SchLibPath        := Ini.ReadString('LibraryPaths', 'SchLibPath', '');
        OpenLibIfNoSchDoc := Ini.ReadInteger('Transaction', 'OpenLibraryIfNoSchDoc', 1);
        SilentMode        := Ini.ReadInteger('Transaction', 'SilentMode', 1);

        WriteBridgeLog(
            LogFilePath,
            'INFO',
            '=== PlaceOrLoadComponent Başladı | LCSC: ' + LcscId + ' | LibRef: ' + LibRef + ' | Action: ' + Action + ' ==='
        );

        UpdateIniResult(Ini, 'RUNNING', Action, 'None', '');

        { 1. Yedek (Fallback) MakeLibrary Akışı Kontrolü }
        if UpperCase(Trim(Action)) = 'FALLBACKMAKELIBRARY' then
        begin
            FallbackOk := ExecuteFallbackMakeLibrary(Ini, LogFilePath);
            if FallbackOk then
                UpdateIniResult(Ini, 'SUCCESS', 'FallbackMakeLibrary', Ini.ReadString('LibraryPaths', 'FallbackSchDocPath', 'MakeLibrary'), '')
            else
                UpdateIniResult(Ini, 'ERROR', 'FallbackMakeLibrary', 'None', 'MakeLibrary başarısız oldu.');
            Exit;
        end;

        { 2. İlgili .SchLib, .PcbLib ve .DbLib Dosyalarını Altium'a Kur / Yenile }
        EnsureLibrariesInstalled(Ini, LogFilePath);

        { Sadece kütüphane yükleme istendiyse burada tamamla }
        if UpperCase(Trim(Action)) = 'LOADONLY' then
        begin
            UpdateIniResult(Ini, 'SUCCESS', 'LoadOnly', SchLibPath, '');
            WriteBridgeLog(LogFilePath, 'INFO', 'Sadece kütüphane yükleme (LoadOnly) başarıyla tamamlandı.');
            if SilentMode = 0 then
                ShowMessage('EasyAltium: ' + LcscId + ' (' + LibRef + ') kütüphaneye eklendi.');
            Exit;
        end;

        { 3. Aktif Şematik (.SchDoc) Kontrolü ve Bileşen Yerleştirme }
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
                    'Bileşen aktif şematiğe yerleştirildi: ' + CurrentSchDoc.DocumentName
                );
            end
            else
            begin
                UpdateIniResult(
                    Ini,
                    'ERROR',
                    'PlaceFailed',
                    CurrentSchDoc.DocumentName,
                    'Sembol kütüphaneden yüklenemedi: ' + LibRef
                );
                WriteBridgeLog(LogFilePath, 'ERROR', 'Sembol kütüphaneden yüklenemedi: ' + LibRef);
                if SilentMode = 0 then
                    ShowMessage('EasyAltium: Sembol kütüphaneden yüklenemedi (' + LibRef + ').');
            end;
        end
        else
        begin
            { Aktif bir .SchDoc açık değilse }
            WriteBridgeLog(LogFilePath, 'INFO', 'Aktif .SchDoc bulunamadı. Kütüphane yüklendi.');
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
                        'EasyAltium: ' + LcscId + ' (' + LibRef + ') kütüphaneye yüklendi. ' +
                        'Yerleştirmek için bir şematik (.SchDoc) açın.'
                    );
            end;
        end;
    finally
        Ini.Free;
    end;
end;

{==============================================================================}
{ YARDIMCI GİRİŞ NOKTASI: RunFallbackMakeLibrary                               }
{ Doğrudan MakeLibrary yedeğini CLI üzerinden çağırmak için alternatif giriş   }
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
        LogFilePath := Ini.ReadString('Transaction', 'LogFilePath', DEFAULT_LOG_FILE);
        if ExecuteFallbackMakeLibrary(Ini, LogFilePath) then
            UpdateIniResult(Ini, 'SUCCESS', 'FallbackMakeLibrary', 'MakeLibrary', '')
        else
            UpdateIniResult(Ini, 'ERROR', 'FallbackMakeLibrary', 'MakeLibrary', 'MakeLibrary hatası');
    finally
        Ini.Free;
    end;
end;

{==============================================================================}
{ TOOLBAR GİRİŞ NOKTASI: LaunchOrShowEasyAltium                                }
{ Altium üst araç çubuğundaki (Toolbar) EasyAltium çip ikonuna tıklandığında   }
{ masaüstü istemci uygulamasını başlatır veya açık olan pencereyi öne getirir. }
{==============================================================================}
procedure LaunchOrShowEasyAltium;
var
    IniPath      : String;
    WorkspaceDir : String;
    HostExePath  : String;
begin
    HostExePath := 'D:\wb\antigravity\easyaltium-q\src\EasyAltium.Host\bin\Debug\net8.0-windows\EasyAltium.exe';

    if not FileExists(HostExePath) then
    begin
        IniPath := ResolveIniFilePath;
        if IniPath <> '' then
        begin
            WorkspaceDir := ExpandFileName(ExtractFilePath(IniPath) + '..\');
            HostExePath := WorkspaceDir + 'src\EasyAltium.Host\bin\Debug\net8.0-windows\EasyAltium.exe';
            if not FileExists(HostExePath) then
                HostExePath := WorkspaceDir + 'EasyAltium.exe';
        end;
    end;

    if FileExists(HostExePath) then
    begin
        RunApplication('"' + HostExePath + '" --show');
    end
    else
    begin
        ShowMessage('EasyAltium istemci uygulaması bulunamadı: ' + HostExePath);
    end;
end;
