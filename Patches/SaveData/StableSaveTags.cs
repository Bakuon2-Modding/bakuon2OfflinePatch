using System;
using System.Collections.Generic;

namespace BakuonOfflinePatch
{
    /// <summary>
    /// 本編は一部の設定を "saveData?tag={userID}_xxx" に保存するが、オフラインの userID は
    /// "@Offline_{名前}" (TitlePatches) なので、入場時や名前変更屋で名前を変えると
    /// エモートチャット文言・マクロ・遺跡の続きから等が別物 (初期値) になってしまう。
    /// また PrivateServerMod (接続時に固有ID) / CheatMod GMモード ("@Master_local_") も
    /// userID を書き換えるため、同じ理由で設定が分裂していた。
    ///
    /// 対策: ES2Settings 生成時 (ES2RedirectPatches) に userID 依存タグを固定キーへ読み替える。
    /// 本編側の Save/Load 呼び出しを個別にパッチせず一点で済む。旧タグのデータは
    /// GameManager.LoadSaveData の前に一度だけ固定キーへ複製する (Migrate)。
    /// </summary>
    public static class StableSaveTags
    {
        public const string FILE = "saveData_offline";
        private const string STABLE_PREFIX = "offline";

        /// <summary>設定類: オフライン/オンライン/GMモード問わず共通化</summary>
        private static readonly string[] SettingSuffixes = { "_emotionChat", "_macro" };

        /// <summary>遺跡の続きから: オフライン中のみ共通化 (オンラインは従来どおり固有IDごと)</summary>
        private static readonly string[] ResumeSuffixes =
        {
            "_latestRoomName", "_resumeSuteageSeed", "_resumeSuteageDungeonMasterDataPropertyName"
        };

        private const string CANVAS_PREFIX = "DrawingCanvas_";

        /// <summary>移行処理中は旧タグを直接読むため読み替えを止める</summary>
        [ThreadStatic] private static bool _suppress;

        /// <summary>タイトルで名前を変えた場合の変更前の名前 (移行元の候補)</summary>
        public static string PreviousPlayerName;

        private static bool _migrated;

        public static string StableTag(string suffix) => STABLE_PREFIX + suffix;

        /// <summary>
        /// userID 依存タグなら固定キーを返す。対象外ならそのまま返す。
        /// </summary>
        public static string Rewrite(string tag)
        {
            if (_suppress || string.IsNullOrEmpty(tag)) return tag;

            foreach (var s in SettingSuffixes)
            {
                if (tag.EndsWith(s, StringComparison.Ordinal))
                    return STABLE_PREFIX + s;
            }

            if (!OnlineMode.IsActive)
            {
                foreach (var s in ResumeSuffixes)
                {
                    if (tag.EndsWith(s, StringComparison.Ordinal))
                        return STABLE_PREFIX + s;
                }
            }

            if (tag.StartsWith(CANVAS_PREFIX, StringComparison.Ordinal))
                return CANVAS_PREFIX + STABLE_PREFIX;

            return tag;
        }

        /// <summary>
        /// 旧 userID タグ → 固定キーへの一回限りの移行。固定キーが既にあれば何もしない。
        /// 移行元の優先順: 変更前の名前 → 現在の userID → ファイル内で唯一の "@Offline_*" タグ。
        /// </summary>
        public static void Migrate(string currentUserID)
        {
            if (_migrated) return;
            _migrated = true;

            _suppress = true;
            try
            {
                string[] allTags = null;
                try
                {
                    if (ES2.Exists(FILE)) allTags = ES2.GetTags(FILE);
                }
                catch (Exception ex)
                {
                    LogHelper.LogWarning($"[StableSaveTags] タグ一覧取得失敗: {ex.Message}");
                }

                var prefixes = new List<string>();
                if (!string.IsNullOrEmpty(PreviousPlayerName)) prefixes.Add("@Offline_" + PreviousPlayerName);
                if (!string.IsNullOrEmpty(currentUserID)) prefixes.Add(currentUserID);

                foreach (var s in SettingSuffixes)
                    MigrateOne(STABLE_PREFIX + s, BuildCandidates(prefixes, s, allTags), CopyStringList);
                foreach (var s in ResumeSuffixes)
                {
                    Action<string, string> copy = s == "_resumeSuteageSeed"
                        ? (Action<string, string>)CopyInt
                        : CopyString;
                    MigrateOne(STABLE_PREFIX + s, BuildCandidates(prefixes, s, allTags), copy);
                }

                var canvasPrefixes = new List<string>();
                foreach (var p in prefixes) canvasPrefixes.Add(CANVAS_PREFIX + p);
                MigrateOne(CANVAS_PREFIX + STABLE_PREFIX,
                    BuildCanvasCandidates(canvasPrefixes, allTags), CopyBytes);
            }
            catch (Exception ex)
            {
                LogHelper.LogError($"[StableSaveTags] 移行エラー: {ex}");
            }
            finally
            {
                _suppress = false;
            }
        }

        private static List<string> BuildCandidates(List<string> prefixes, string suffix, string[] allTags)
        {
            var list = new List<string>();
            foreach (var p in prefixes) list.Add(p + suffix);

            // 既に過去に改名済みで旧名が分からない場合: ファイル内の "@Offline_*{suffix}" が1つだけならそれを使う
            if (allTags != null)
            {
                string only = null; int count = 0;
                foreach (var t in allTags)
                {
                    if (t.StartsWith("@Offline_", StringComparison.Ordinal) && t.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        only = t; count++;
                    }
                }
                if (count == 1) list.Add(only);
                else if (count > 1) LogHelper.LogInfo($"[StableSaveTags] {suffix} の旧タグが複数あり自動選択しません ({count}件)");
            }
            return list;
        }

        private static List<string> BuildCanvasCandidates(List<string> tags, string[] allTags)
        {
            var list = new List<string>(tags);
            if (allTags != null)
            {
                string only = null; int count = 0;
                foreach (var t in allTags)
                {
                    if (t.StartsWith(CANVAS_PREFIX + "@Offline_", StringComparison.Ordinal)) { only = t; count++; }
                }
                if (count == 1) list.Add(only);
            }
            return list;
        }

        private static void MigrateOne(string stableTag, List<string> candidates, Action<string, string> copy)
        {
            string dst = FILE + "?tag=" + stableTag;
            if (ES2.Exists(dst)) return;

            foreach (var c in candidates)
            {
                string src = FILE + "?tag=" + c;
                if (!ES2.Exists(src)) continue;
                try
                {
                    copy(src, dst);
                    LogHelper.LogInfo($"[StableSaveTags] {c} → {stableTag} へ移行");
                }
                catch (Exception ex)
                {
                    LogHelper.LogWarning($"[StableSaveTags] {c} の移行失敗: {ex.Message}");
                }
                return;
            }
        }

        private static void CopyStringList(string src, string dst) => ES2.Save(ES2.LoadList<string>(src), dst);
        private static void CopyString(string src, string dst) => ES2.Save(ES2.Load<string>(src), dst);
        private static void CopyInt(string src, string dst) => ES2.Save(ES2.Load<int>(src), dst);
        private static void CopyBytes(string src, string dst) => ES2.Save(ES2.LoadArray<byte>(src), dst);
    }
}
