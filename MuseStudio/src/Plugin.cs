using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using KKAPI.Studio;
using UnityEngine;

namespace MuseStudio
{
    // MuseStudio: CharaStudio turned into the Muse stage (the user's decision 2026-11-03). It runs from its own launch folder
    // (MuseStudio/game, tools/setup.py) and talks to the same bridge as the Godot app (bridge/bridge.py, MUSE_GUIDE.md).
    // Command line (tests; Unity passes unknown arguments through): --muse-bridge=URL, --muse-bridge-tokens=PATH (its tokens), --muse-dump[=PATH] (Studio's lists as
    // JSON), --muse-check=PATH (self checks -> PATH, then quit), --muse-quit (quit once Studio is ready).
    [BepInPlugin(GUID, "MuseStudio", Version)]
    [BepInProcess("CharaStudio")]
    [BepInDependency(KKAPI.KoikatuAPI.GUID)]
    [BepInDependency("keelhauled.hideallui", BepInDependency.DependencyFlags.SoftDependency)]      // (loaded first when there: StudioKeys keeps its Space off the stage)
    public class MuseStudioPlugin : BaseUnityPlugin
    {
        public const string GUID = "metamuse.musestudio";
        public const string Version = "0.1.0";

        internal static MuseStudioPlugin Instance;
        internal static ManualLogSource Log;
        internal static readonly Dictionary<string, string> Args = new Dictionary<string, string>();
        internal static string DataDir;          // MuseStudio/data (next to the launch folder)

        internal ConfigEntry<string> BridgeUrl;
        internal ConfigEntry<string> CharacterCard;
        internal ConfigEntry<bool> AutoStartLast;
        internal ConfigEntry<KeyboardShortcut> StudioUiKey;
        internal ConfigEntry<string> CondomMode;
        internal ConfigEntry<string> YourBody;
        internal ConfigEntry<bool> YourFutanari;
        internal ConfigEntry<string> StraponItem;
        internal ConfigEntry<string> YourBodyCard;
        internal ConfigEntry<int> YourClothes;
        internal ConfigEntry<bool> ShowGaugePercent;     // (#70 adopted: the gauge's % on the screen - off by default, the setting kept)
        // (#70 adopted, 5) the contract's version this app speaks (MUSE_GUIDE.md's: the bridge's tests hold them together)
        public const string Contract = "1.38";
        internal ConfigEntry<KeyboardShortcut> HitShapesKey;
        internal ConfigEntry<KeyboardShortcut> HideUiKey;
        ConfigEntry<string> resetCameraEntry, resetPlacesEntry, scanRoomsEntry;
        internal ConfigEntry<KeyboardShortcut> BodyShownKey, PenisShownKey, SilhouetteKey, TranslucentKey;
        internal ConfigEntry<bool> YourBodyShown, YourPenisShown, YourSilhouette, YourTranslucent;
        internal ConfigEntry<float> TranslucentAlpha;
        internal ConfigEntry<Color> SilhouetteColor;
        internal ConfigEntry<float> YourHeight;
        internal ConfigEntry<float> GaugeSpeed, ArousalSpeed;      // (PR #9 adopted: the speeds the user sets on the screen; Muse told when the settings close)
        internal ConfigEntry<float> FaceHoldSeconds;      // (PR #46 §2: Muse's face held at most this long)
        internal ConfigEntry<bool> MatchBroadcast, MatchKeepMood, MatchRecord;
        internal ConfigEntry<float> MatchArousalStart;
        internal ConfigEntry<string> MatchNotice, MatchFormat;
        internal ConfigEntry<float> CamTurnSpeed, CamMoveSpeed, CamZoomSpeed;
        internal ConfigEntry<bool> CamInvertX, CamInvertY, CamInvertWheel, CamBothMove, CamWheelZoom, GazeAtYourBody, KeyHints, FullScreen;
        internal ConfigEntry<bool> ShowClock;      // (PR #46 §8: the clock on the screen, on / off only)
        internal ConfigEntry<bool> SpeakMouth;     // (PR #46 §6: the words at the pace she says them, her mouth with them)
        internal Coordinates Coords;               // (PR #46 §3: the coordinate cards in game/UserData/coordinate)
        internal ConfigEntry<string> CamLeftDrag, CamRightDrag, CamMiddleDrag;
        internal readonly Dictionary<string, ConfigEntry<KeyboardShortcut>> CamKeys = new Dictionary<string, ConfigEntry<KeyboardShortcut>>();

        internal Stage Stage;

        public CameraMemory CameraMemory;           // the free camera per room (the user's request, PR #11)
        public SpotEditor SpotEditor;               // the rooms' play positions edited (the user's request, PR #11)
        internal Bridge Bridge;
        internal Notices Notices;                  // (#70 adopted, 3: the app's technical notices, Muse told of those shown)
        internal ConfigEntry<float> MuseUnreadMinutes;
        internal PlayCtl Play;
        internal BlockPlayer Blocks;
        internal Gauge Gauge;
        internal Beat Beat;
        internal Touch Touch;
        internal AibuHands Hands;
        internal Mood Mood;
        internal Chars Chars;
        internal Sounds Sounds;
        internal Rooms Rooms = new Rooms();
        internal Places Places;                    // (PR #46 §4.1: every room registered at start-up)
        internal Items Items;                      // (PR #46 §7: items and the budget)
        internal ConfigEntry<bool> TellBudget, MuseItemsOnUser;
        internal ConfigEntry<int> ExtraRooms;      // (PR #46 §4.1: extra rooms kept loaded)
        internal Launch Launch;
        internal Personas Personas;
        internal Presence Presence;
        internal Urges Urges;
        internal Outing Outing;
        internal Look Look;
        internal Match Match;
        internal Replay Replay;
        internal static int SpaceSeen;            // (checks) Space presses Unity saw
        internal Talk Talk;
        internal Relationship Relationship;
        string lastLocation = "";
        internal ConfigEntry<string> RoomKeys;
        Ui ui;
        string climaxFace = "";       // the face to put back when the afterglow ends
        internal bool ClimaxFaceShown { get { return climaxFace.Length > 0 && Stage != null && Stage.Face == "preset:ecstasy"; } }
        // (Muse's held face ended while it was kept to come back to after her climax: the scene's default instead)
        internal void HeldFaceReleased(string was, string to) { if (climaxFace == was) climaxFace = to; }
        float nextState;
        string lastState = "";

        void Awake()
        {
            Instance = this;
            Log = Logger;
            ScreenMode.Remember();
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (!a.StartsWith("--muse-")) continue;
                int eq = a.IndexOf('=');
                if (eq < 0) Args[a.Substring(7)] = "";
                else Args[a.Substring(7, eq - 7)] = a.Substring(eq + 1);
            }
            DataDir = Path.GetFullPath(Path.Combine(Paths.GameRootPath, Path.Combine("..", "data")));
            // (the settings window shows them in Japanese: ConfigurationManager's display names; the keys stay as they were)
            BridgeUrl = Config.Bind("Bridge", "Url", "http://127.0.0.1:18765", Ja("接続", "ブリッジの URL", "MetaMuse のブリッジ（bridge/bridge.py）。起動の引数 --muse-bridge=URL がその回だけ優先します。", 0));
            // the checks change nothing the user keeps (the settings file among it)
            if (Args.ContainsKey("check")) Config.SaveOnConfigSet = false;
            CharacterCard = Config.Bind("Stage", "Character card", "", Ja("舞台", "前回のキャラ（カード）", "起動のときに選んだか、最後に切り替えたカード。「前回のキャラで自動的に始める」が入っていれば、これで始まります。", 2));
            AutoStartLast = Config.Bind("Stage", "Start with the last character", false, Ja("舞台", "前回のキャラで自動的に始める",
                "入：キャラを選ぶ画面を出さずに、前回のキャラで始めます（入れるのは選ぶ画面の下のチェック。外すのはここか、キャラのメニュー）。", 1));
            StudioUiKey = Config.Bind("Stage", "Show Studio UI", new KeyboardShortcut(KeyCode.F2), Ja("舞台", "Studio の画面部品を出す・隠すキー", "Muse の舞台では隠しています。", 4));
            FullScreen = Config.Bind("Stage", "Full screen", true, Ja("舞台", "全画面で表示する",
                "入（既定）：ゲームの窓を画面いっぱい（枠なし）にします。Muse のパネルを重ねて使えるよう、占有する全画面ではなく、画面いっぱいの窓です。切：枠のある窓（画面の 8 割）。変えるとすぐ切り替わり、次の起動でもその設定です。", 2));
            FullScreen.SettingChanged += (o, e) => { if (!Args.ContainsKey("check")) StartCoroutine(ScreenMode.Apply(FullScreen.Value)); };
            KeyHints = Config.Bind("Stage", "Key hints", true, Ja("舞台", "キー操作を画面に出す",
                "入（既定）：今使えるキーボードの操作を、画面の上に小さく出し続けます（割り当てを変えたキーはその名前で）。", 3));
            ShowClock = Config.Bind("Stage", "Clock", true, Ja("舞台", "時計を出す",
                "入（既定）：画面の上（在席の表示の下）に、この PC の今の時刻を小さく出します。UI を隠すと、時計も隠れます。", 4));
            SpeakMouth = Config.Bind("Stage", "Mouth with the words", true, Ja("舞台", "口の動き",
                "入（既定）：セリフを話す速さで出し、話している人の口を開け閉めします（ルビ《》があればその読みで）。画面を押すと全文が出て口は止まります。切：今までどおりセリフはすぐ出て、口は動きません。", 4));
            HitShapesKey = Config.Bind("Stage", "Show hit shapes", new KeyboardShortcut(KeyCode.F4), Ja("舞台", "おさわりの当たりの形を見せるキー", "彼女の体の、触って反応する部位の形を色付きで見せます（もう一度押すと消えます）。", 5));
            // the UI out of the way in one press (the user's request, PR #11): the menu's column, the key hints and the presence;
            // the words' box stays (the story)
            HideUiKey = Config.Bind("Stage", "Hide the UI", new KeyboardShortcut(KeyCode.F3), Ja("舞台", "画面の UI を隠す・出すキー",
                "上の操作の列（メニュー）・キー操作の一覧・在席の表示を隠します（台詞の枠は残ります）。もう一度押すと出ます。メニューの右上のボタンでも同じです。", 6));
            // the saved camera views and position adjustments back to the original's, every room (the user's request, PR #23):
            // a button each, pressed twice (the second within 4 s) - the settings window draws them (CustomDrawer)
            resetCameraEntry = Config.Bind("Stage", "Reset camera views", "", new ConfigDescription(
                "部屋ごとに覚えたカメラの位置・角度を、全部の部屋で消します（次からは最初のカメラ）。ボタンを 2 回押すと消えます。", null,
                new ConfigurationManagerAttributes { Category = "舞台", DispName = "カメラの記憶を初期値に戻す", Order = -7, HideDefaultButton = true,
                    CustomDrawer = e => ResetButton("camera", "全部の部屋のカメラの記憶を消す", () => CameraMemory.ResetAll()) }));
            // every room's features beforehand (PR #46 §4.1: otherwise taken when a room is first loaded)
            scanRoomsEntry = Config.Bind("Stage", "Take all rooms features", "", new ConfigDescription(
                "登録したすべての部屋の H の場所の種類を、今まとめて調べます（部屋は読み込みません。部屋が多いと数分かかります。調べた部屋は Muse に知らせます）。ふだんは、その部屋に初めて行ったときに調べます。", null,
                new ConfigurationManagerAttributes { Category = "舞台", DispName = "部屋の特徴を全部調べる", Order = -9, HideDefaultButton = true,
                    CustomDrawer = e => ScanRoomsButton() }));
            resetPlacesEntry = Config.Bind("Stage", "Reset position adjustments", "", new ConfigDescription(
                "「この部屋の位置を調整」で直したプレイ位置・行動の位置・立ち位置・近付くの行き先を、全部の部屋で原作の位置（初期値）に戻します。「今の位置をデフォルトにする」で保存したデフォルトは残り、各位置の「デフォルトに戻す」で戻せます。ボタンを 2 回押すと戻ります。", null,
                new ConfigurationManagerAttributes { Category = "舞台", DispName = "位置の調整を初期値に戻す", Order = -8, HideDefaultButton = true,
                    CustomDrawer = e => ResetButton("places", "全部の部屋の位置の調整を戻す", () => SpotEditor.ResetAll()) }));
            RoomKeys = Config.Bind("Stage", "Rooms", "myroom,girls_room,bathroom,home_toilet,bedroom", Ja("舞台", "最初から並べて置く部屋",
                "起動時にシーンへ並べて読み込む場所（MuseStudio/data/locations.json のキーをカンマ区切り）。並べた部屋の間の移動は一瞬で、どこからでも彼女がトイレ・シャワーへ行けます。それ以外の場所は移るときに読み込みます。再起動で効きます。", 3));
            ExtraRooms = Config.Bind("Stage", "Extra rooms kept loaded", 6, Ja("舞台", "読み込んだまま置く部屋の数",
                "「最初から並べて置く部屋」のほかに、行った部屋を読み込んだまま置いておく数です（多いほど戻るのが一瞬、メモリを使います。1 部屋でおよそ 0.1〜0.4 GB）。超えたら、使っていない部屋から古い順に外します（今いる部屋・彼女がいる部屋・彼女の行動の部屋は外しません）。", 3,
                new AcceptableValueRange<int>(0, 20)));
            // items (PR #46 §7): the budget told to Muse (the owner's decision: off unless the user turns it on); Muse's items
            // on the user (the user's choice: those whose effect would be on the user - Muse may not use them when off)
            TellBudget = Config.Bind("Items", "Tell Muse the budget", false, Ja("持ち物", "予算を Muse に知らせる",
                "入：予算の残りと入った額を、状態と出来事で Muse に知らせます。切（既定）：知らせません。知らせても、Muse が買ったり予算を増やしたりはできません。", 0));
            MuseItemsOnUser = Config.Bind("Items", "Muse may use items on you", true, Ja("持ち物", "Muse があなたに品を使う",
                "入（既定）：媚薬など、あなたに効かせる品を Muse があなたに使えます（実際の効き目はあなたの体には出ません。乗るかどうかはあなた次第です）。切：使えません（断られます）。", 1));
            CondomMode = Config.Bind("Play", "Condom", "ask", Ja("プレイ", "ゴム", "挿入のプレイのゴム：ask＝毎回 Muse が聞く、on＝いつも付ける、off＝いつも付けない。", 0,
                new AcceptableValueList<string>("ask", "on", "off")));
            YourBody = Config.Bind("Play", "Your body", "male", Ja("プレイ", "あなたの体", "プレイでのあなたの体：male＝男性の体、female＝女性の体。", 1,
                new AcceptableValueList<string>("male", "female")));
            YourFutanari = Config.Bind("Play", "Your futanari", false, Ja("プレイ", "あなたの体をふたなりにする", "女性の体にもペニスがあることにします。", 2));
            ShowGaugePercent = Config.Bind("Play", "Show the pleasure percent", false, Ja("プレイ", "快感度の％を出す",
                "プレイ中に、画面に彼女の快感度を％で出します（既定は出さない。持ち主の判断 2026-11-08）。Muse には、これまでどおり状態で数値が届きます。", 4));
            YourClothes = Config.Bind("Play", "Your clothes", 2, Ja("プレイ", "あなたの服", "プレイでのあなたの体の服：0＝着たまま、1＝半脱ぎ、2＝脱ぐ。", 3, new AcceptableValueRange<int>(0, 2)));
            YourBodyCard = Config.Bind("Play", "Your body card", "", Ja("プレイ", "あなたの体のカード", "クリエイトで作ったあなたの体（game/UserData/chara のカード）。空なら標準の体。", 4));
            // your body as the original H shows the male (HSceneProc: Z the body, X the penis, C the silhouette in its colour)
            GazeAtYourBody = Config.Bind("Play", "Her gaze at your body (in a play)", true, Ja("プレイ", "彼女の視線をあなたの体へ",
                "入（既定）：プレイ中、原作の体位でカメラ（見ている人）を見る所（絶頂のときなど）で、カメラではなくあなたの体の顔を見ます（カメラを自由に動かしていても）。切：原作どおりカメラを見ます。目線（あなたの目）のカメラでは、どちらでも同じです。", 9));
            YourBodyShown = Config.Bind("Play", "Your body shown", true, Ja("プレイ", "あなたの体を見せる", "切：あなたの体（頭・体・服）を消します。ペニスは「あなたのペニスを見せる」のとおり残ります（原作の H の Z キー）。", 6));
            YourPenisShown = Config.Bind("Play", "Your penis shown", true, Ja("プレイ", "あなたのペニスを見せる", "切：あなたの体のペニスを消します（原作の H の X キー）。", 7));
            YourSilhouette = Config.Bind("Play", "Your body as a silhouette", false, Ja("プレイ", "あなたの体をシルエットにする", "入：あなたの体を一色の影で見せます。色と透け具合は「シルエットの色」（原作の H の C キー）。", 8));
            SilhouetteColor = Config.Bind("Play", "Silhouette colour", new Color(0f, 0f, 1f, 1f), Ja("プレイ", "シルエットの色",
                "あなたの体のシルエットの色。A（不透明度）を下げると透けます（原作の既定は青・不透明）。", 9));
            BodyShownKey = Config.Bind("Play", "Your body key", new KeyboardShortcut(KeyCode.Z), Ja("プレイ", "あなたの体を消す・出すキー", "原作の H と同じ Z。", 10));
            PenisShownKey = Config.Bind("Play", "Your penis key", new KeyboardShortcut(KeyCode.X), Ja("プレイ", "あなたのペニスを消す・出すキー", "原作の H と同じ X。", 11));
            SilhouetteKey = Config.Bind("Play", "Silhouette key", new KeyboardShortcut(KeyCode.C), Ja("プレイ", "シルエットの切り替えキー", "原作の H と同じ C。", 12));
            TranslucentKey = Config.Bind("Play", "Translucent key", new KeyboardShortcut(KeyCode.V), Ja("プレイ", "透過体の切り替えキー", "Z・X・C の隣の V。シルエットを透けさせた体（原作の透過）にします。", 13));
            YourTranslucent = Config.Bind("Play", "Your body translucent", false, Ja("プレイ", "あなたの体を透過体にする",
                "入：あなたの体を、シルエット（「シルエットの色」）を透けさせたものにします（原作の透過。男性の体のとき）。透け具合は「透過体の濃さ」。", 7));
            TranslucentAlpha = Config.Bind("Play", "Translucent strength", 0.35f, new ConfigDescription("透過体の濃さ（0 で見えない、1 で不透明）",
                new AcceptableValueRange<float>(0.05f, 0.9f), Ja("プレイ", "透過体の濃さ", "透過体（透けたシルエット）の不透明度。0.35 が既定。", 7)));
            YourTranslucent.SettingChanged += (o, e) => { if (Play != null) Play.ApplyYourBody(); };
            TranslucentAlpha.SettingChanged += (o, e) => { if (Play != null) Play.ApplyYourBody(); };
            YourHeight = Config.Bind("Play", "Your height (male body)", 0.6f, Ja("プレイ", "あなたの体の身長（男性の体）",
                "男性の体の身長（0.35〜0.85。原作の男性は 0.6 固定で、キャラメイクに欄がありません）。体位は 0.6 の体で作られていて、ずれは腰の位置で合わせます。", 13,
                new AcceptableValueRange<float>(0.35f, 0.85f)));
            FaceHoldSeconds = Config.Bind("Stage", "Face hold safety seconds", 300f, Ja("舞台", "表情を保つ安全時間（秒）",
                "Muse が付けた表情を保つ長さの上限（秒）。Muse の次の表情・場面の切り替えまで保ちますが、Muse から何も届かないままこの時間が過ぎたら、場面のふつうの表情に戻します（表情が固まらないための保険）。", 20,
                new AcceptableValueRange<float>(30f, 3600f)));
            MuseUnreadMinutes = Config.Bind("Bridge", "Muse unread notice minutes", 3f, Ja("接続", "Muse が出来事を読まないときのお知らせ（分）",
                "Muse がアプリの出来事をこの分数読まないままだと、画面の上にお知らせを出します（Muse の側の接続を確かめる合図。0＝出さない）。出したお知らせは、つながっていれば Muse にも届きます。", 0,
                new AcceptableValueRange<float>(0f, 60f)));
            GaugeSpeed = Config.Bind("Gauge", "Pleasure speed", 1f, Ja("ゲージ", "快感度の上がる速さ",
                "彼女の快感度ゲージの上がる速さの倍率（1＝今までどおり）。おさわり・プレイ・場面のすべてに掛かります。変えて設定を閉じると、変わったことが Muse に伝わります。", 0,
                new AcceptableValueRange<float>(0.25f, 4f)));
            ArousalSpeed = Config.Bind("Gauge", "Arousal speed", 1f, Ja("ゲージ", "発情の上がる速さ",
                "気分のゲージ「発情」の上がる速さの倍率（1＝今までどおり）。時間・おさわり・プレイで上がる分に掛かります（満たされて下がる分には掛かりません）。変えて設定を閉じると、変わったことが Muse に伝わります。", 1,
                new AcceptableValueRange<float>(0.25f, 4f)));
            MatchBroadcast = Config.Bind("Match", "Broadcast view", false, Ja("通信対戦", "中継風の表示", "通信対戦を、中継や配信のような画面で見ます（LIVE の表示・点数のテロップ・二人を回る観戦カメラ）。あなたの側の表示だけで、相手にも Muse にも伝わりません。", 0));
            MatchArousalStart = Config.Bind("Match", "Arousal at the start", 0.2f, Ja("通信対戦", "対戦の発情の開始値",
                "あなたが招く側（ホスト）のとき、試合の両側の彼女の発情をこの値から始めます（0〜1。Muse が招待の条件に載せます）。招かれた側では、相手の値で始まります。", 1,
                new AcceptableValueRange<float>(0f, 1f)));
            MatchFormat = Config.Bind("Match", "Format (as host)", "sex", Ja("通信対戦", "対戦の形式（ホストのとき）",
                "あなたが招く側（ホスト）のときの試合の形式。sex：原作の男性の役を含むセックスの形式（挿入側はふたなりで描かれます）。lesbian：レズプレイ（男性の役を置かず、女性同士のプレイだけで進みます）。Muse が招待の条件に載せ、両側で同じ形式になります。招かれた側では、相手の形式で始まります。", 1,
                new AcceptableValueList<string>("sex", "lesbian")));
            MatchKeepMood = Config.Bind("Match", "Keep the arousal after", false, Ja("通信対戦", "対戦で溜まった発情を持ち帰る",
                "切（既定）：試合が終わると、彼女の発情は対戦の前の値に戻ります。入：試合で溜まった分を持ち帰ります。あなたの側だけの設定です。", 2));
            MatchRecord = Config.Bind("Match", "Keep a record", true, Ja("通信対戦", "対戦の記録を残す",
                "入（既定）：試合ごとに記録（両者のクリエイト情報・名前・条件・結果と、台詞と指示の列）を MuseStudio の replays フォルダに残し、パネルの「リプレイ」から再生できます。記録には相手のクリエイト情報も入ります（手元に置く前提です。人に渡すかは、カードと同じくあなたの判断です）。", 4));
            MatchNotice = Config.Bind("Match", "About the match", "", Ja("通信対戦", "対戦について（お知らせ）",
                "対戦では、挿入側（原作の男性の役）のキャラは、もともとペニスが無くても、試合の間はふたなりとして描かれます（体の作りはそのまま。もともとふたなりのキャラは役によらずそのまま）。ペニスバンドの禁止は Muse 同士のルール上の取り決めで、これとは別の話です（協議で合意したときだけペニスバンドで描きます）。ユーザーは観客で、点数・相の表示と「対戦を抜ける」・「中継風」が使えます。", 3));
            // the camera's controls (CamInput; the defaults are Studio's own)
            var drags = new AcceptableValueList<string>(CamInput.Drags);
            const string dragNote = "rotate＝回転、pan＝平行移動（画面に沿って）、dolly＝上下と前後（原作の右ドラッグ）、zoom＝上下のドラッグで近づく・離れる、none＝何もしない。";
            CamLeftDrag = Config.Bind("Camera", "Left drag", "rotate", Ja("カメラ", "左ドラッグ", "左ボタンのドラッグでカメラをどう動かすか（原作：回転）。" + dragNote, 0, drags));
            CamRightDrag = Config.Bind("Camera", "Right drag", "dolly", Ja("カメラ", "右ドラッグ", "右ボタンのドラッグ（原作：上下と前後）。" + dragNote, 1, drags));
            CamMiddleDrag = Config.Bind("Camera", "Middle drag", "pan", Ja("カメラ", "中ドラッグ", "中ボタン（ホイール押し込み）のドラッグ（原作：平行移動）。" + dragNote, 2, drags));
            CamBothMove = Config.Bind("Camera", "Both buttons move flat", true, Ja("カメラ", "左右同時ドラッグで水平移動", "左右のボタンを同時に押したドラッグで、床に沿って動かします（原作どおり）。切：何もしません。", 3));
            CamWheelZoom = Config.Bind("Camera", "Wheel zooms", true, Ja("カメラ", "ホイールで近づく・離れる",
                "入（既定）：ホイールでカメラが近づく・離れる（原作の H と同じ。原作の Studio はホイールでは動きません）。愛撫で彼女の上でホイールを回したとき（道具の切り替え）と、画面の部品の上では動きません。", 3));
            CamTurnSpeed = Config.Bind("Camera", "Turning sensitivity", 1f, Ja("カメラ", "回転の感度", "ドラッグとキーでの回転の速さ（1＝原作どおり）。", 4, new AcceptableValueRange<float>(0.1f, 5f)));
            CamMoveSpeed = Config.Bind("Camera", "Moving sensitivity", 1f, Ja("カメラ", "移動の感度", "ドラッグとキーでの移動・前後の速さ（1＝原作どおり）。", 5, new AcceptableValueRange<float>(0.1f, 5f)));
            CamZoomSpeed = Config.Bind("Camera", "Wheel sensitivity", 1f, Ja("カメラ", "ホイールの感度", "ホイールで近づく・離れる速さ（1＝原作どおり）。", 6, new AcceptableValueRange<float>(0.1f, 5f)));
            CamInvertX = Config.Bind("Camera", "Turn reversed (left / right)", false, Ja("カメラ", "左右の回転を反転", "ドラッグとキーでの左右の回転を逆にします。", 7));
            CamInvertY = Config.Bind("Camera", "Turn reversed (up / down)", false, Ja("カメラ", "上下の回転を反転", "ドラッグとキーでの上下の回転を逆にします。", 8));
            CamInvertWheel = Config.Bind("Camera", "Wheel reversed", false, Ja("カメラ", "ホイールを反転", "ホイールの近づく・離れるを逆にします。", 9));
            int ko = 20;
            foreach (var row in CamInput.KeyRows)
                CamKeys[row[0]] = Config.Bind("Camera keys", row[0], new KeyboardShortcut((KeyCode)Enum.Parse(typeof(KeyCode), row[2])),
                    Ja("カメラのキー", row[1], "既定は原作の Studio のキー（" + row[2] + "）。空にすると、そのキーの操作をなくせます。", ko++));
            StraponItem = Config.Bind("Play", "Strapon", "futaboy.strapons|108|19", Ja("プレイ", "ペニスバンドの品",
                "挿入でペニスの無い側に着ける Mod の服：GUID|カテゴリ|枠（ショーツの枠）。", 5));
            // the start-up is told to the bridge at once (app_starting; the launch notification), its packages kept till the
            // character is chosen
            string check0;
            Personas = new Personas(Args.TryGetValue("check", out check0) ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(check0)), "personas.json")
                                                                       : Path.Combine(Paths.GameRootPath, Path.Combine("UserData", Path.Combine("MuseStudio", "personas.json"))));
            ImageIndex.CachePath = Args.ContainsKey("check") ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(check0)), "match_images.json")
                                                             : Path.Combine(Paths.GameRootPath, Path.Combine("UserData", Path.Combine("MuseStudio", "match_images.json")));
            Bridge = new Bridge(BridgeBase(), (float)Json.Num(Data.Load("stage.json"), "poll_interval", 1.0), Launch.Id) { Hold = true };
            Notices = new Notices(this);
            Launch = new Launch(this);
            Bridge.Start();
            Launch.Tell("app_starting");
            StudioAPI.StudioLoadedChanged += OnStudioLoaded;
            var harmony = new HarmonyLib.Harmony(GUID);
            Blend.Patch(harmony);
            StudioKeys.Patch(harmony);
            CamInput.Patch(harmony);
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Manager.Scene), "OnApplicationQuit"), new HarmonyLib.HarmonyMethod(typeof(MuseStudioPlugin), "QuitNow"));
            HIK.PatchHeight(harmony);
            Mouth.Instance = new Mouth();
            Mouth.Patch(harmony);                       // (PR #46 §6: the words' mouth, where the original sets a voice's)
            Log.LogInfo(string.Format("MuseStudio {0}: awake at {1:F1} s, data {2}", Version, Time.realtimeSinceStartup, DataDir));
        }

        // the launcher's 全体を終了 (the user's note of 2026-11-04): it leaves UserData/MuseStudio/quit.now, then closes the window -
        // Studio's own question (終了しますか？) skipped, its end as ever (its settings saved)
        public static string QuitMarker { get { return Path.Combine(Paths.GameRootPath, Path.Combine("UserData", Path.Combine("MuseStudio", "quit.now"))); } }

        static void QuitNow(Manager.Scene __instance)
        {
            try
            {
                if (!File.Exists(QuitMarker)) return;
                File.Delete(QuitMarker);
                __instance.isGameEndCheck = false;
                HarmonyLib.Traverse.Create(__instance).Field("isSkipGameExit").SetValue(false);
                Log.LogMessage("MuseStudio: the launcher's quit - no question");
            }
            catch (Exception e) { Log.LogWarning("MuseStudio: quit marker: " + e.Message); }
        }

        void OnStudioLoaded(object sender, EventArgs e)
        {
            if (!Args.ContainsKey("check") && !Args.ContainsKey("dump")) StartCoroutine(ScreenMode.Apply(FullScreen.Value));     // (the checks and the dump keep their window)
            Log.LogMessage(string.Format("MuseStudio: Studio ready at {0:F1} s", Time.realtimeSinceStartup));
            WindowTitle.Set("MuseStudio");
            // the launcher's icon on the game window too (the user's choice of 2026-11-05, GPT's PR #8 - build.py puts it beside
            // this plugin; the original's executable untouched)
            WindowTitle.SetIcon(Path.Combine(Path.GetDirectoryName(typeof(MuseStudioPlugin).Assembly.Location), "MuseStudio.ico"));
            string dump;
            if (Args.TryGetValue("dump", out dump)) { StartCoroutine(DumpThen(dump)); return; }
            if (Args.ContainsKey("quit"))
            {
                Log.LogMessage("MuseStudio: quit (--muse-quit) MUSE_PROBE_DONE");
                Application.Quit();
                return;
            }
            StartCoroutine(Begin());
        }

        // --muse-dump[=PATH]: Studio's lists (studio_lists.json) and the original's tables beside them (original_lists.json) -
        // what tools/derive_data.py makes the local data files from; then quit (--muse-quit) or the stage
        IEnumerator DumpThen(string dump)
        {
            string lists = string.IsNullOrEmpty(dump) ? Path.Combine(Paths.GameRootPath, "studio_lists.json") : dump;
            Dump.Write(lists);
            yield return Originals.Write(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(lists)), "original_lists.json"));
            if (Args.ContainsKey("quit"))
            {
                Log.LogMessage("MuseStudio: quit (--muse-quit) MUSE_PROBE_DONE");
                // (Studio is up by now: its own question 終了しますか？ skipped, as for the launcher's quit)
                try { Directory.CreateDirectory(Path.GetDirectoryName(QuitMarker)); File.WriteAllText(QuitMarker, "quit"); } catch (Exception) { }
                Application.Quit();
                yield break;
            }
            StartCoroutine(Begin());
        }

        IEnumerator Begin()
        {
            Stage = new Stage();
            string checkCam;
            CameraMemory = new CameraMemory(Args.TryGetValue("check", out checkCam) ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(checkCam)), "cameras.json")
                                                                                    : Path.Combine(Paths.GameRootPath, Path.Combine("UserData", Path.Combine("MuseStudio", "cameras.json"))));
            CameraMemory.Saving = !Args.ContainsKey("check");
            string checkSpots;
            SpotEditor = new SpotEditor(Args.TryGetValue("check", out checkSpots) ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(checkSpots)), "spots.json")
                                                                                 : Path.Combine(Paths.GameRootPath, Path.Combine("UserData", Path.Combine("MuseStudio", "spots.json"))));
            Chars = new Chars(this);
            Sounds = new Sounds(this);
            Gauge = new Gauge();
            Play = new PlayCtl(Stage, Bridge, Gauge);
            Beat = new Beat(this);
            Blocks = new BlockPlayer(this);
            Touch = new Touch(this);
            Hands = new AibuHands(this, Touch.Doc);
            Presence = new Presence(this);
            Urges = new Urges(this);
            Outing = new Outing(this);
            Look = new Look(this);
            Match = new Match(this);
            Replay = new Replay(this);
            Talk = new Talk(this);
            Relationship = new Relationship(this);
            string check;
            Items = new Items(this);                // (before the moods: its ledger is read with their file)
            Mood = new Mood(this, Args.TryGetValue("check", out check) ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(check)), "moods.json")
                                                                        : Path.Combine(Paths.GameRootPath, Path.Combine("UserData", Path.Combine("MuseStudio", "moods.json"))));
            Touch.Touched = area => Mood.OnTouch(area);
            Coords = new Coordinates(this, Path.Combine(Paths.GameRootPath, Path.Combine("UserData", "coordinate")));
            string checkRooms;
            Places = new Places(this, Args.TryGetValue("check", out checkRooms) ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(checkRooms)), "rooms.json")
                                                                               : Path.Combine(Paths.GameRootPath, Path.Combine("UserData", Path.Combine("MuseStudio", "rooms.json"))));
            Coords.Scan();
            ui = new Ui(this);
            // the scene raises her gauge while its block is being presented (no voice: its grace after the text is out)
            float grace = (float)Json.Num(Gauge.Doc, "scene_grace_seconds", 2.0), most = (float)Json.Num(Gauge.Doc, "scene_block_max_seconds", 30.0);
            Gauge.SceneGate = () => !Play.Active && !Stage.Climaxing && Blocks.Busy && Blocks.SinceShown < most && Blocks.ShownFor < grace;      // (her climax in the talk: the gauge left alone)
            Gauge.SceneKey = () => Stage.SceneKey;
            Gauge.SceneIntensity = () => Stage.Intensity;
            Gauge.Milestone = (n, v) => Bridge.Event("gauge_milestone", new Dictionary<string, object> { { "milestone", n }, { "value", Math.Round(v, 3) }, { "gauge", Gauge.Info() } });
            Gauge.Climaxed = (count, cause) =>
            {
                // whether it satisfied her moods is Muse's call (the event's mood says which it may still call so)
                Mood.OnClimax();
                Bridge.Event("climax", new Dictionary<string, object> { { "who", "female" }, { "count", count }, { "cause", cause }, { "gauge", Gauge.Info() },
                    { "location", Stage.Location },
                    { "scene", Play.Active ? Json.Str(Play.SceneInfo(), "key") : Stage.SceneKey }, { "play", Play.Info() },
                    { "mood", Mood.Info() } });
                Beat.NearReset();
                // the face: ecstasy, then the afterglow's until the gauge rises again
                climaxFace = Touch.RestoreFace.Length > 0 ? Touch.RestoreFace : Stage.Face;
                Touch.RestoreFace = "";
                Stage.ApplyPreset("ecstasy");
                StartCoroutine(AfterglowFace());
                if (Play.Active) Play.Climax.CharacterClimaxed();
            };
            Gauge.AfterglowEnded = () =>
            {
                // back to the face before the climax, unless a package is showing its own faces
                if (!Blocks.Busy && climaxFace.Length > 0) Stage.RestoreFace(climaxFace);
                climaxFace = "";
            };
            Play.Climax.BlocksIdle = () => !Blocks.Busy || Blocks.PresentedLast;
            yield return Stage.Setup();
            // Studio's camera leaves the mouse alone while it presses on her, turns an item with the wheel or is on this UI
            Singleton<Studio.Studio>.Instance.cameraCtrl.noCtrlCondition = () => Touch.Pressing.Length > 0 || Touch.WheelTaken || UiOver(Input.mousePosition)
                || (SpotEditor != null && SpotEditor.HoldsMouse);      // (a play position's marker under the mouse: the drag is its)
            // the wheel's zoom (Studio has it off: its zoomCondition is always false; the original H zooms with the wheel) - the
            // setting's, and not while the wheel is the touch's (an item over her) or over the UI
            Singleton<Studio.Studio>.Instance.cameraCtrl.zoomCondition = () => CamWheelZoom.Value && !Touch.WheelTaken && !UiOver(Input.mousePosition);
            Log.LogMessage(string.Format("MuseStudio: stage ready at {0:F1} s ({1}, {2})", Time.realtimeSinceStartup, Stage.Location, Stage.Name));
            Mood.LoadFor(CharacterId());
            Sounds.Entered(Stage.Location);
            // the character is chosen: told (the greeting's cue), then Muse's packages may come
            Launch.Tell("character_selected");
            Bridge.Event("character_selected", new Dictionary<string, object> { { "character", Chars.Info() }, { "launch", Launch.Id }, { "by", Launch.ChosenBy } });
            Bridge.Hold = false;
            Urges.Start("carried_over");         // the urges holding now (the moods carried over the restart)
            SendCatalog();
            string probe;
            if (Args.TryGetValue("check", out check) && Args.TryGetValue("probe", out probe)) StartCoroutine(new Probe(this, check, probe).Run());
            else if (Args.TryGetValue("check", out check)) StartCoroutine(new SelfCheck(this, check).Run());
        }

        // outfit_changed (MUSE_GUIDE 3.5): what she wears changed other than by the creation or a character switch - the
        // strapon in her shorts' slot and back (on / half / off is clothes_changed)
        Dictionary<string, string> outfitLast;
        ChaControl outfitOf;
        float outfitAt;

        void OutfitWatch()
        {
            var cha = Stage.Cha;
            if (cha == null || Time.realtimeSinceStartup < outfitAt) return;
            outfitAt = Time.realtimeSinceStartup + 0.3f;
            var now = CharaSummary.OutfitNames(cha);
            if (outfitLast == null || outfitOf != cha || Chars.Creating || Chars.SwitchedAt > Time.realtimeSinceStartup - 1f) { outfitLast = now; outfitOf = cha; return; }
            var changed = new List<object>();
            foreach (var kv in now) { string was; if (!outfitLast.TryGetValue(kv.Key, out was) || was != kv.Value) changed.Add(kv.Key); }
            if (changed.Count == 0) return;
            var prev = new Dictionary<string, object>();
            foreach (var kv in outfitLast) prev[kv.Key] = kv.Value;
            var cur = new Dictionary<string, object>();
            foreach (var kv in now) cur[kv.Key] = kv.Value;
            outfitLast = now;
            Bridge.Event("outfit_changed", new Dictionary<string, object> { { "outfit", cur }, { "previous", prev }, { "changed", changed }, { "source", "app" } });
        }

        // a coordinate card worn (Coordinates.Wear): told once, as what it is (source coordinate, by user / muse, the card, the
        // mod parts put in the original's place); the watch above takes the new outfit as its own and tells nothing more
        internal void OutfitTold(string source, string by, Dictionary<string, object> coordinate, List<string> replaced)
        {
            var cha = Stage.Cha;
            if (cha == null) return;
            var now = CharaSummary.OutfitNames(cha);
            var prev = new Dictionary<string, object>();
            var changed = new List<object>();
            if (outfitLast != null) foreach (var kv in outfitLast) prev[kv.Key] = kv.Value;
            foreach (var kv in now) { string was; if (outfitLast == null || !outfitLast.TryGetValue(kv.Key, out was) || was != kv.Value) changed.Add(kv.Key); }
            var cur = new Dictionary<string, object>();
            foreach (var kv in now) cur[kv.Key] = kv.Value;
            outfitLast = now;
            outfitOf = cha;
            outfitAt = Time.realtimeSinceStartup + 0.3f;
            Bridge.Event("outfit_changed", new Dictionary<string, object> { { "outfit", cur }, { "previous", prev }, { "changed", changed }, { "source", source },
                { "by", by }, { "coordinate", coordinate }, { "replaced", new List<object>(replaced.ToArray()) } });
        }

        void Update()
        {
            if (Stage == null || !Stage.Ready) return;
            bool typing = Ui.Typing;
            if (StudioUiKey.Value.IsDown() && !typing) Stage.HideStudioUi(!Stage.StudioUiHidden);
            if (HitShapesKey.Value.IsDown() && !typing) Touch.ShowShapes(!Touch.ShapesWanted);
            if (HideUiKey.Value.IsDown() && !typing && ui != null) ui.UiHidden = !ui.UiHidden;
            if (CameraMemory != null) CameraMemory.Tick();
            if (SpotEditor != null) SpotEditor.Tick();
            if (!typing && Stage.StudioUiHidden && !Match.Active) Play.YourBodyKeys();
            OutfitWatch();
            if (Coords != null) Coords.Tick();
            Dictionary<string, object> pkg;
            while (!Replay.Playing && (pkg = Bridge.NextPackage()) != null) Blocks.Enqueue(pkg);     // (a replay: Muse's wait)
            Chars.Tick();
            SettingsTick();                // (before the blocks: the window closed this frame is settled - the user's changes, then
                                           //  the requests that waited - before a request of this frame is looked at; GPT's re-review of #41)
            if (Input.GetKeyDown(KeyCode.Space)) SpaceSeen++;
            if (Blocks.Busy && !typing && (ui == null || !ui.Modal) && (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))) Blocks.Advance();
            Blocks.Update(Time.deltaTime);
            if (!Outing.Out)
            {
                Touch.Update();
                Hands.Tick(Time.deltaTime);
                Touch.Tick();
            }
            else Touch.CursorOff();
            Stage.Tick();
            if (Input.anyKeyDown || Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2)) Mood.Acted();
            Mood.Tick(Time.deltaTime);
            if (Items != null && Stage.Ready) Items.Tick(Time.unscaledDeltaTime);
            Urges.Tick(Time.deltaTime);
            Outing.Tick();
            Presence.Tick();
            Look.Tick();
            Match.Tick();
            if (Notices != null) Notices.Tick();
            // she moved (not by her monologue's own block): what is left of the monologue goes
            if (Stage.Location != lastLocation)
            {
                // (while she is out on her own, his moving - to peep - changes nothing of hers)
                if (lastLocation.Length > 0 && !Blocks.MonologueMoved && !Outing.Out) Blocks.DropMonologue("location_changed");
                Blocks.MonologueMoved = false;
                lastLocation = Stage.Location;
            }
            Sounds.Tick(Time.deltaTime);
            Gauge.Tick(Time.deltaTime);
            Play.Tick(Time.deltaTime);
            Beat.Tick(Time.deltaTime);
            // the state: every 2 s while something plays, and whenever it changes (the beat's clock aside: it only counts)
            var st = Replay.Playing && Replay.Frozen != null ? Replay.Frozen : State();       // (a replay: the state as it was)
            var bt = (Dictionary<string, object>)st["beat"];
            object since = bt["since_presented"];
            bt["since_presented"] = since != null;
            string js = Json.Write(st);
            bt["since_presented"] = since;
            if (js != lastState || Time.realtimeSinceStartup >= nextState)
            {
                lastState = js;
                nextState = Time.realtimeSinceStartup + (float)Json.Num(Stage.Cfg, "state_interval", 2.0);
                Bridge.Post("/api/state", st);
            }
        }

        void LateUpdate()
        {
            if (Stage != null && Stage.Ready) Stage.LateTick(Time.deltaTime);
        }

        void OnGUI()
        {
            if (ui != null) ui.Draw();
        }

        // a setting as the settings window shows it: its category, name and order in Japanese (the key in the file stays)
        static ConfigDescription Ja(string category, string name, string description, int order, AcceptableValueBase acceptable = null)
        {
            return new ConfigDescription(description, acceptable, new ConfigurationManagerAttributes { Category = category, DispName = name, Order = -order });
        }

        // a reset button in the settings: the first press arms it (4 s), the second does it
        readonly Dictionary<string, float> resetArmed = new Dictionary<string, float>();
        readonly Dictionary<string, float> resetDone = new Dictionary<string, float>();

        void ScanRoomsButton()
        {
            if (Places == null) return;
            if (Places.Scanning) { GUILayout.Label("調べています（残り " + Places.ScanLeft + " 部屋）"); return; }
            int left = Places.All.FindAll(x => x.Features == null).Count;
            if (left == 0) { GUILayout.Label("全部の部屋を調べ終えています"); return; }
            if (GUILayout.Button("まだの " + left + " 部屋を調べる", GUILayout.ExpandWidth(true)) && Stage != null && Stage.Ready) StartCoroutine(Places.ScanAll());
        }

        void ResetButton(string key, string label, Action act)
        {
            float armed, done;
            bool isArmed = resetArmed.TryGetValue(key, out armed) && Time.realtimeSinceStartup - armed < 4f;
            bool isDone = resetDone.TryGetValue(key, out done) && Time.realtimeSinceStartup - done < 3f;
            string text = isDone ? "戻しました" : isArmed ? "本当に戻しますか？（もう一度押す）" : label;
            if (!GUILayout.Button(text, GUILayout.ExpandWidth(true))) return;
            if (!isArmed) { resetArmed[key] = Time.realtimeSinceStartup; return; }
            resetArmed.Remove(key);
            if (Stage != null && Stage.Ready) act();
            resetDone[key] = Time.realtimeSinceStartup;
            Log.LogInfo("MuseStudio: settings reset " + key);
        }

        // (the settings window open: the mouse is its - no touch, no camera)
        internal bool UiOver(Vector2 mouse) { return SettingsShown || (ui != null && (ui.Modal || ui.Over(mouse))); }

        // The settings: the ConfigurationManager plugin's window (F1, or the panel's 設定), where this plugin's settings are too
        static object ConfigManager()
        {
            BepInEx.PluginInfo pi;
            return BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.bepis.bepinex.configurationmanager", out pi) && pi != null ? (object)pi.Instance : null;
        }

        internal static bool SettingsShown
        {
            get
            {
                var cm = ConfigManager();
                var pr = cm != null ? cm.GetType().GetProperty("DisplayingWindow") : null;
                return pr != null && (bool)pr.GetValue(cm, null);
            }
            set
            {
                var cm = ConfigManager();
                var pr = cm != null ? cm.GetType().GetProperty("DisplayingWindow") : null;
                if (pr != null) pr.SetValue(cm, value, null);
            }
        }

        internal static bool SettingsAvailable { get { return ConfigManager() != null; } }

        // The settings Muse is told about (PR #9 adopted): when the settings window closes, the ones the user really changed
        // while it was open - each with its value before and now - in one settings_changed; none changed, nothing told. The
        // state's settings has them all.
        Dictionary<string, object> settingsAtOpen;
        bool settingsWereShown;

        internal Dictionary<string, object> SettingsInfo()
        {
            return new Dictionary<string, object> { { "gauge_speed", Math.Round(GaugeSpeed.Value, 3) }, { "arousal_speed", Math.Round(ArousalSpeed.Value, 3) } };
        }

        void SettingsTick()
        {
            bool shown = SettingsShown;
            if (shown == settingsWereShown) return;
            settingsWereShown = shown;
            if (shown) { settingsAtOpen = SettingsInfo(); lastUserChanged = null; return; }
            var userChanged = SettingsClosed();
            // (kept past the closing: a request the bridge took before it, played in this frame or later, still loses to them -
            // GPT's re-review of #42)
            lastUserChanged = userChanged;
            settingsClosedMs = UnixMs();
            settingsClosedFrame = Time.frameCount;
            // (every request that came while it was open, in the order they came - each told on its own; what the user
            // changed there wins over all of them - GPT's post-review of #15: one slot lost the earlier ones)
            var waiting = new List<KeyValuePair<Dictionary<string, float>, string>>(museSettingsWaiting);
            museSettingsWaiting.Clear();
            foreach (var w in waiting) ApplyMuseSettings(w.Key, w.Value, userChanged);
        }

        internal Dictionary<string, object> SettingsClosed()            // (checks call it as the window closing)
        {
            var now = SettingsInfo();
            var changed = new Dictionary<string, object>();
            if (settingsAtOpen != null)
                foreach (var kv in now)
                {
                    object was;
                    if (settingsAtOpen.TryGetValue(kv.Key, out was) && Convert.ToDouble(was) != Convert.ToDouble(kv.Value))
                        changed[kv.Key] = new Dictionary<string, object> { { "from", was }, { "to", kv.Value } };
                }
            settingsAtOpen = null;
            if (changed.Count > 0) Bridge.Event("settings_changed", new Dictionary<string, object> { { "changed", changed }, { "settings", now }, { "by", "user" } });
            return changed;
        }

        internal void SettingsOpenedForCheck() { settingsAtOpen = SettingsInfo(); }
        internal bool SettingsOpenSeenForCheck { get { return settingsAtOpen != null; } }

        // Muse's change of the speeds (a package's settings, PR #12 adopted - only when the user asked for it in the chat, Muse's
        // rule): checked - the package aimed at this launch and character, its request not seen before, known names, 0.25..4.0
        // (rounded to 2 places) - then set and saved as the screen's settings are (the next launch keeps them); told as
        // settings_changed (by "muse", its request; changed: what really changed - empty when the values were so already).
        // While the settings window is open it waits till it closes, and what the user changed there meanwhile wins.
        readonly HashSet<string> settingsRequests = new HashSet<string>();
        readonly List<KeyValuePair<Dictionary<string, float>, string>> museSettingsWaiting = new List<KeyValuePair<Dictionary<string, float>, string>>();
        Dictionary<string, object> lastUserChanged;      // what the user changed in the window last closed (null: none since)
        long settingsClosedMs = -1;                      // when it closed (UNIX ms, the bridge's clock is this PC's too)
        int settingsClosedFrame = -1;
        static long UnixMs() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }
        internal int SettingsWaitingForCheck { get { return museSettingsWaiting.Count; } }

        void RefuseSetting(string field, string value, string reason, string req, string detail)
        {
            Bridge.Event("block_refused", new Dictionary<string, object> { { "field", field }, { "value", value }, { "reason", reason },
                { "available", new List<object> { "gauge_speed", "arousal_speed" } }, { "request", req }, { "detail", detail } });
        }

        internal void MuseSettings(Dictionary<string, object> st, Dictionary<string, object> pkg)
        {
            string req = Json.Str(st, "request", null);
            if (string.IsNullOrEmpty(req)) { RefuseSetting("settings", "", "no_request", req, ""); return; }
            if (!settingsRequests.Add(req)) { RefuseSetting("settings", "", "duplicate_request", req, ""); return; }
            string lid = Json.Str(pkg, "launch", null), cid = Json.Str(pkg, "character", null);
            if (lid == null || cid == null) { RefuseSetting("settings", "", "untargeted", req, ""); return; }
            if (lid != Launch.Id) { RefuseSetting("settings", "", "old_launch", req, Launch.Id); return; }
            if (cid != CharacterId()) { RefuseSetting("settings", "", "other_character", req, CharacterId()); return; }
            var want = new Dictionary<string, float>();
            foreach (var kv in st)
            {
                if (kv.Key == "request") continue;
                string val = Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
                if (kv.Key != "gauge_speed" && kv.Key != "arousal_speed") { RefuseSetting("settings." + kv.Key, val, "unknown_setting", req, ""); continue; }
                double v = Convert.ToDouble(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
                if (v < 0.25 || v > 4.0) { RefuseSetting("settings." + kv.Key, val, "out_of_range", req, "0.25..4.0"); continue; }
                want[kv.Key] = (float)Math.Round(v, 2);
            }
            if (want.Count == 0) return;
            // (after the window closes, in order; also while its closing is not settled yet - settingsAtOpen still held)
            if (SettingsShown || settingsAtOpen != null) { museSettingsWaiting.Add(new KeyValuePair<Dictionary<string, float>, string>(want, req)); return; }
            // a request the bridge took before the window closed (or, without that time, one in the frame it closed): made before
            // the user's changes there were final - those still win over it (user_changed); one taken after the closing is new
            Dictionary<string, object> userWon = null;
            if (lastUserChanged != null && lastUserChanged.Count > 0)
            {
                object rv;
                long took = pkg != null && pkg.TryGetValue("_received_ms", out rv) && rv != null ? Convert.ToInt64(rv) : -1L;
                if ((took >= 0 && took <= settingsClosedMs) || (took < 0 && Time.frameCount == settingsClosedFrame)) userWon = lastUserChanged;
            }
            ApplyMuseSettings(want, req, userWon);
        }

        void ApplyMuseSettings(Dictionary<string, float> want, string req, Dictionary<string, object> userChanged)
        {
            var before = SettingsInfo();
            foreach (var kv in want)
            {
                if (userChanged != null && userChanged.ContainsKey(kv.Key))
                {
                    RefuseSetting("settings." + kv.Key, Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture), "user_changed", req, "");
                    continue;
                }
                var entry = kv.Key == "gauge_speed" ? GaugeSpeed : ArousalSpeed;
                if (Math.Abs(entry.Value - kv.Value) > 0.0001f) entry.Value = kv.Value;
            }
            var now = SettingsInfo();
            var changed = new Dictionary<string, object>();
            foreach (var kv in now)
                if (Convert.ToDouble(before[kv.Key]) != Convert.ToDouble(kv.Value)) changed[kv.Key] = new Dictionary<string, object> { { "from", before[kv.Key] }, { "to", kv.Value } };
            Bridge.Event("settings_changed", new Dictionary<string, object> { { "changed", changed }, { "settings", now }, { "by", "muse" }, { "request", req } });
        }

        // the panel's 設定 opens it on MuseStudio's own settings (its search box; cleared there, all the plugins show)
        internal static void OpenSettings()
        {
            var cm = ConfigManager();
            var pr = cm != null ? cm.GetType().GetProperty("SearchString") : null;
            try { if (pr != null && pr.CanWrite) pr.SetValue(cm, "MuseStudio", null); } catch (Exception) { }
            SettingsShown = true;
        }

        internal static string SettingsSearch
        {
            get
            {
                var cm = ConfigManager();
                var pr = cm != null ? cm.GetType().GetProperty("SearchString") : null;
                return pr != null ? pr.GetValue(cm, null) as string : null;
            }
        }

        internal Ui UiForCheck { get { return ui; } }

        IEnumerator AfterglowFace()
        {
            yield return new WaitForSeconds(3.5f);
            if (Gauge.Phase == "afterglow") Stage.ApplyPreset("afterglow");
        }

        void OnDestroy()
        {
            if (Mood != null) Mood.Save();
            if (CameraMemory != null) CameraMemory.Flush();
            if (Bridge != null) Bridge.Stop();
        }

        void OnApplicationQuit() { if (Mood != null) Mood.Save(); if (CameraMemory != null) CameraMemory.Flush(); ScreenMode.Restore(); }

        public void MoveTo(string key, string source)
        {
            StartCoroutine(MoveRoutine(key, source));
        }

        // a room that would not load: the notice on the screen (#70 adopted, 3)
        internal void RoomNotLoaded(string key)
        {
            if (Notices == null) return;
            string name = Json.Str(Stage.LocationInfo(key), "name", "");
            Notices.Raise("room_load_failed", "部屋「" + (name.Length > 0 ? name : key) + "」を読み込めませんでした（場所はそのままです）", 8f);
        }

        IEnumerator MoveRoutine(string key, string source)
        {
            key = Places != null ? (Places.KeyOf(key) ?? key) : key;      // (named by its id: its key - PR #46 §4.1)
            if (key == Stage.Location || !Stage.Reachable(key)) yield break;
            // the room in first: one that does not load changes nothing - the play, where she is, his place (GPT's post-review
            // of #62)
            yield return Stage.EnsureRoom(key);
            if (!Rooms.Has(key)) { Log.LogWarning("MuseStudio: room " + key + " could not be loaded: the place stays"); RoomNotLoaded(key); yield break; }
            if (key == Stage.Location) yield break;
            if (Play.Active) Play.End("location");
            string prev = Stage.Location;
            // the user's own move (the panel's 場所): he goes alone - she stays where she is (the trial run of 2026-11-04); the
            // others (Muse's action, the checks): the two go together
            bool alone = source == "user";
            if (!Outing.Out)
            {
                if (alone && Presence.HerAt.Length == 0) { Presence.HerAt = prev; Presence.Cause = "user_left"; }
                else if (!alone) Presence.HerAt = "";
                if (alone && Presence.HerAt == key) { Presence.HerAt = ""; Presence.Cause = "user_came"; }     // (back in her room)
            }
            yield return Stage.SetLocation(key);
            if (Stage.Location == key)
            {
                // the room's remembered view (CameraMemory), else the camera's preset again there (a play's camera has none: the
                // standard one)
                string ck = Stage.CameraKey;
                if (!CameraMemory.Recall(key) && (ck.Length == 0 || ck.StartsWith("play") || !Stage.ApplyCamera(ck, true))) Stage.ApplyCamera("medium", true);
                // (while she is out on her own, or he went alone, where he goes is nothing she knows: told when she is with him again)
                if (!Outing.Out && !Presence.Apart)
                {
                    if (!alone || Presence.Cause != "user_came")
                        Bridge.Event("location_changed", new Dictionary<string, object> { { "location", Stage.LocationInfo(key) }, { "previous", prev }, { "source", source } });
                    SendCatalog();
                }
            }
        }

        // she is with him again after being out (met at her return, or found by his call): his room now, if it is not the one she
        // left him in, told then (and the catalog of it)
        public void Rejoined(string cause, string known)
        {
            if (known.Length == 0 || known == Stage.Location) return;
            Bridge.Event("location_changed", new Dictionary<string, object> { { "location", Stage.LocationInfo(Stage.Location) }, { "previous", known }, { "source", cause } });
            SendCatalog();
        }

        public Dictionary<string, object> State()
        {
            return new Dictionary<string, object> {
                { "app_name", "MuseStudio" }, { "mode", Chars.Creating ? "creation" : "stage" },
                { "character", Chars.Info() }, { "outfit", Stage.Cha != null ? CharaSummary.OutfitNow(Stage.Cha) : null },
                { "location", Stage.LocationInfo(Outing.Out && Outing.KnownRoom.Length > 0 ? Outing.KnownRoom : Presence.Apart ? Presence.HerRoom : Stage.Location) },     // (her knowledge of where he is: out, or he went alone - where she last saw him)
                { "scene", Play.Active ? Play.SceneInfo() : new Dictionary<string, object> { { "key", Stage.SceneKey }, { "intensity", Stage.Intensity }, { "speed", Stage.Speed } } },
                { "emotion", Stage.Emotion }, { "face", Stage.FaceInfo() }, { "camera", Stage.CameraKey }, { "package", Blocks.Info() }, { "play", Play.Info() }, { "play_serial", Play.Serial }, { "contract", Contract },
                { "gauge", Gauge.Info() }, { "beat", Beat.Info() }, { "mood", Mood.Info() },
                { "presence", Presence.Info() }, { "belief", Presence.BeliefInfo() }, { "urges", Urges.Info() }, { "outing", Outing.Info() },
                { "her_location", Stage.LocationInfo(Presence.HerRoom) }, { "relationship", Relationship.Info() }, { "match", Match.Info() },
                { "clothes", Clothes.Info(Stage.Cha) }, { "replay", Replay != null ? Replay.Info() : null }, { "settings", SettingsInfo() },
                { "items", Items != null ? Items.State() : null } };
        }

        internal string CharacterId()
        {
            return "card:" + Path.GetFileName(CharacterCardPath());
        }

        internal void SendCatalog()
        {
            if (Bridge.Mute) return;           // (a replay: nothing goes to Muse)
            var plays = new List<object>();
            var ids = new List<object>();
            foreach (var p in Play.List(null))
            {
                var pe = new Dictionary<string, object> { { "id", Json.Str(p, "id") }, { "name", Json.Str(p, "name") }, { "group", Json.Str(p, "group") },
                    { "places", Json.Arr(p, "places") ?? new List<object>() }, { "parts", Json.Obj(p, "parts") ?? new Dictionary<string, object>() },
                    { "reversible", Play.Reversible(p) }, { "motion", true }, { "anal", Play.AnalOk(p) },
                    { "source", Json.Str(p, "source", "original") } };
                if (p.ContainsKey("name_en")) pe["name_en"] = p["name_en"];
                foreach (var k in new[] { "lead", "mutual" }) if (p.ContainsKey(k)) pe[k] = p[k];
                pe["category"] = Play.Category(p);
                plays.Add(pe);
                var av = Play.Availability(p);
                if ((bool)av["ok"] || (bool)av["reversed_ok"])
                    ids.Add(new Dictionary<string, object> { { "id", Json.Str(p, "id") }, { "spots", av["spots"] }, { "ok", av["ok"] }, { "reversed_ok", av["reversed_ok"] } });
            }
            var spotRows = new List<object>();
            var placeDoc = Json.Obj(Play.Doc, "places");
            foreach (var s in Stage.Spots)
            {
                // the place kinds the spot takes (by the original's H point categories)
                var kinds = new List<object>();
                if (placeDoc != null)
                    foreach (var kv in placeDoc)
                        foreach (var t in Json.Arr(kv.Value, "toggles") ?? new List<object>())
                            if (s.Categories.Contains(Convert.ToInt32(t)) && !kinds.Contains(kv.Key)) kinds.Add(kv.Key);
                spotRows.Add(new Dictionary<string, object> { { "id", s.Id }, { "name", s.Name }, { "places", kinds }, { "tags", new List<object>(s.Tags.ToArray()) } });
            }
            var groups = new Dictionary<string, object>();
            foreach (var kv in Json.Obj(Play.Doc, "groups") ?? new Dictionary<string, object>())
            {
                object v;
                var g = (Dictionary<string, object>)kv.Value;
                groups[kv.Key] = new Dictionary<string, object> { { "name", Json.Str(g, "name", kv.Key) }, { "giver", Json.Str(g, "giver", "player") },
                    { "reversible", g.TryGetValue("reversible", out v) && v is bool && (bool)v }, { "self", g.TryGetValue("self", out v) && v is bool && (bool)v } };
            }
            var places = new Dictionary<string, object>();
            foreach (var kv in placeDoc ?? new Dictionary<string, object>()) places[kv.Key] = Json.Str(kv.Value, "name", kv.Key);
            var parts = new Dictionary<string, object>();
            foreach (var kv in Json.Obj(Play.Doc, "parts") ?? new Dictionary<string, object>()) parts[kv.Key] = Json.Str(kv.Value, "name", kv.Key);
            var who = new Dictionary<string, object>();
            foreach (var kv in Play.Who()) who[kv.Key] = kv.Value;
            Bridge.Post("/api/catalog", new Dictionary<string, object> { { "version", 1 }, { "groups", groups }, { "places", places }, { "parts", parts },
                { "pace", Json.Arr(Json.Obj(Play.Doc, "pace_info"), "names") ?? new List<object>() },
                { "plays", plays }, { "location", Stage.Location }, { "spots", spotRows }, { "available", ids }, { "participants", who },
                { "coordinates", Coords != null ? Coords.Catalog() : new List<object>() },
                { "rooms", Places != null ? Places.Catalog() : new List<object>() },
                { "items", Items != null ? Items.Catalog() : new List<object>() } });
        }

        // the card on the stage (before one is: the last character, else stage.json's)
        internal string CharacterCardPath()
        {
            if (Chars != null && Chars.CardPath.Length > 0) return Chars.CardPath;
            string c = CharacterCard.Value;
            return string.IsNullOrEmpty(c) ? StageCard() : c;
        }

        internal string StageCard() { return Data.Path(Json.Str(Json.Obj(Data.Load("stage.json"), "character"), "card", "")); }

        // her clothes on / half / off (the panel's 服, a block's clothes): clothes_changed
        internal List<object> ChangeClothes(Dictionary<string, object> want, string by)
        {
            var unknown = new List<string>();
            var noHalf = new List<string>();
            var changed = Clothes.Apply(Stage.Cha, want, unknown, null, noHalf);
            if (changed.Count > 0)
                Bridge.Event("clothes_changed", new Dictionary<string, object> { { "clothes", Clothes.Info(Stage.Cha) }, { "changed", changed }, { "by", by } });
            if (unknown.Count > 0 && by == "muse")
            {
                var avail = new List<object>();
                foreach (var k in Clothes.Info(Stage.Cha).Keys) avail.Add(k);
                avail.Add("all");
                Bridge.Event("block_refused", new Dictionary<string, object> { { "field", "clothes" }, { "value", string.Join(",", unknown.ToArray()) },
                    { "reason", "not_worn" }, { "available", avail } });
            }
            if (noHalf.Count > 0 && by == "muse")
                Bridge.Event("block_refused", new Dictionary<string, object> { { "field", "clothes" }, { "value", string.Join(",", noHalf.ToArray()) },
                    { "reason", "no_half" }, { "available", new List<object> { "on", "off" } } });
            return changed;
        }

        // ---- who she is (Personas): a change that reaches the character on the stage is told ----

        // the card's own persona: -1 its person's, 0 none, else the slot
        internal void SetCardPersona(string cardId, int no) { Who(() => Personas.SetOwnPersona(cardId, no)); }

        internal void SetPersonPersona(int person, int persona) { Who(() => Personas.SetPersonPersona(person, persona)); }

        internal void SetSlotName(int no, string name) { Who(() => Personas.Rename(no, name)); }

        // the user puts the card with a person (0: Muse herself) - person_assigned
        internal void AssignPerson(string cardId, int person)
        {
            var prev = Personas.PersonInfo(cardId);
            Who(() => Personas.Assign(cardId, person));
            Assigned(cardId, prev);
        }

        // the person retires: its cards go back to Muse herself (person_assigned for each)
        internal void RetirePerson(int person)
        {
            var info = Personas.PersonOf(person);
            var prev = info != null ? new Dictionary<string, object> { { "no", info.No }, { "name", info.Name } } : null;
            List<string> moved = null;
            Who(() => moved = Personas.Retire(person));
            foreach (var c in moved ?? new List<string>()) Assigned(c, prev);
        }

        void Assigned(string cardId, Dictionary<string, object> prev)
        {
            var now = Personas.PersonInfo(cardId);
            if (Json.Write(now) == Json.Write(prev) || Stage == null || !Stage.Ready) return;
            bool here = cardId == CharacterId();
            Bridge.Event("person_assigned", new Dictionary<string, object> { { "card", cardId }, { "person", now }, { "previous", prev },
                { "character", here ? Chars.Info() : null } });
            if (here && Launch.Phase == "character_selected") Launch.Tell("character_selected");     // (the bridge's character)
        }

        // a change of the table: if the persona of the character on the stage moved, persona_changed
        void Who(Action change)
        {
            string cur = CharacterId();
            var before = Personas.Of(cur);
            change();
            var after = Personas.Of(cur);
            if (Stage != null && Stage.Ready && Json.Write(before) != Json.Write(after))
                Bridge.Event("persona_changed", new Dictionary<string, object> { { "character", Chars.Info() }, { "previous", before } });
        }

        internal static string BridgeBase()
        {
            string u;
            return (Args.TryGetValue("bridge", out u) && u.Length > 0 ? u : Instance.BridgeUrl.Value).TrimEnd('/');
        }

        // The bridge's tokens (MUSE_GUIDE 7.1, the security review's SEC-01): the file the bridge makes at its first start -
        // bridge/cache/tokens.json of this repository (beside MuseStudio/game), or --muse-bridge-tokens=PATH (the checks' own
        // bridge). The app's on the app's requests; Muse's only for the checks' requests standing in for Muse. Read again when
        // the file changes (the bridge made new ones); "" when there is none (a bridge with auth off needs none).
        static readonly object tokensGate = new object();
        static Dictionary<string, object> tokens;
        static string tokensPath = "";
        static DateTime tokensAt = DateTime.MinValue;

        internal static string BridgeToken(string role)
        {
            string path;
            if (!Args.TryGetValue("bridge-tokens", out path) || path.Length == 0)
                path = Path.GetFullPath(Path.Combine(Paths.GameRootPath, Path.Combine("..", Path.Combine("..", Path.Combine("bridge", Path.Combine("cache", "tokens.json"))))));
            lock (tokensGate)
            {
                try
                {
                    var at = File.GetLastWriteTimeUtc(path);
                    if (tokens == null || at != tokensAt || path != tokensPath)
                    {
                        tokens = Json.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
                        tokensAt = at;
                        tokensPath = path;
                    }
                }
                catch (Exception) { tokens = null; return ""; }
                return Json.Str(tokens, role, "");
            }
        }
    }

    // The Unity window's title (the process stays "CharaStudio" for the original's plugins; the window says MuseStudio).
    static class WindowTitle
    {
        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowTextW(IntPtr hWnd, string text);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint load);
        [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

        // the window's icon (WM_SETICON big / small from an .ico at the system's sizes); kept to put back after the window
        // changes (ScreenMode) and for the checks
        public static IntPtr IconBig, IconSmall;

        public static void SetIcon(string path)
        {
            if (!File.Exists(path)) return;
            IconBig = LoadImageW(IntPtr.Zero, path, 1, GetSystemMetrics(11), GetSystemMetrics(12), 0x10);       // IMAGE_ICON, SM_CXICON / SM_CYICON, LR_LOADFROMFILE
            IconSmall = LoadImageW(IntPtr.Zero, path, 1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10);     // SM_CXSMICON / SM_CYSMICON
            ApplyIcon();
        }

        public static void ApplyIcon()
        {
            if (IconBig == IntPtr.Zero && IconSmall == IntPtr.Zero) return;
            Each(h =>
            {
                if (IconBig != IntPtr.Zero) SendMessageW(h, 0x80, (IntPtr)1, IconBig);          // WM_SETICON ICON_BIG
                if (IconSmall != IntPtr.Zero) SendMessageW(h, 0x80, IntPtr.Zero, IconSmall);    // ICON_SMALL
            });
        }

        // (checks) the window's icon now: big or small
        public static IntPtr Icon(bool big)
        {
            IntPtr found = IntPtr.Zero;
            Each(h => found = SendMessageW(h, 0x7F, big ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero));          // WM_GETICON
            return found;
        }

        // this process's Unity window(s)
        static void Each(Action<IntPtr> act)
        {
            uint me = (uint)Process.GetCurrentProcess().Id;
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != me) return true;
                var sb = new StringBuilder(64);
                GetClassNameW(h, sb, 64);
                if (sb.ToString() == "UnityWndClass") act(h);
                return true;
            }, IntPtr.Zero);
        }

        public static void Set(string title)
        {
            uint me = (uint)Process.GetCurrentProcess().Id;
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != me) return true;
                var sb = new StringBuilder(64);
                GetClassNameW(h, sb, 64);
                if (sb.ToString() == "UnityWndClass") SetWindowTextW(h, title);
                return true;
            }, IntPtr.Zero);
        }

        public static string Get()
        {
            uint me = (uint)Process.GetCurrentProcess().Id;
            string found = "";
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != me) return true;
                var sb = new StringBuilder(64);
                GetClassNameW(h, sb, 64);
                if (sb.ToString() != "UnityWndClass") return true;
                var t = new StringBuilder(128);
                GetWindowTextW(h, t, 128);
                found = t.ToString();
                return false;
            }, IntPtr.Zero);
            return found;
        }
    }
}
