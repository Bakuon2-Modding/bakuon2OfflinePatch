using System.Collections.Generic;

namespace BakuonOfflinePatch
{
    /// <summary>
    /// クラスアビリティ取得状態の整合化 (読み込み直後に実行)。
    ///
    /// 【背景】
    /// バニラはログインのたびに GameManager.CreateHoldUnitPrefab が
    ///   ・取得済みアビリティの重複除去
    ///   ・残りクラスポイントの再計算 (CalculateLevelPoint = jobLevel - 1 - 取得数)
    /// を行うため、取得処理の多重実行などでセーブが壊れても次回ログインで自己修復していた。
    /// OfflinePatch の読み込みは全キャラを Lv1 で作ってから RenewStockUnitDataFromStringList で
    /// 保存値を上書きするだけなので、この自己修復が働かず「ClassLv10 でも全アビリティを
    /// 取得できない」状態が恒久化していた (スキルツリー9ノード・Lv10 で9ポイントと余りゼロ)。
    ///
    /// 【対処】バニラのログイン時と同じく重複を除去して残りポイントを再計算する。加えて
    ///   ・NONE (バニラの ResetJobLevelUpPoint が「消さずに NONE で埋める」バグの残骸。
    ///     バニラでは重複除去後も1件残り、恒久的に1ポイント失われていた) を除去
    ///   ・スキルツリーに存在しない ID (IsVisibleClassAbility=false) を除去
    ///     (バニラは見つけると一覧を全消去=全額返還。ここでは該当分だけ外す)
    ///   ・装着スロットのうち未取得アビリティを外し、スロット数を3に揃える
    ///   ・それでも取得数が多すぎる (ポイントが負) 場合は全額返還
    /// </summary>
    internal static class ClassAbilityRepair
    {
        private const int SlotCount = 3;

        internal static void RepairAll()
        {
            var gm = SingletonMonoBehaviour<GameManager>.Instance;
            if (gm == null || gm.stockUnitDataList == null) return;
            foreach (UnitData unit in gm.stockUnitDataList)
            {
                if (unit == null) continue;
                try
                {
                    Repair(unit);
                }
                catch (System.Exception ex)
                {
                    LogHelper.LogError("[ClassAbilityRepair] " + unit.unitID + " の整合化に失敗: " + ex.Message);
                }
            }
        }

        private static void Repair(UnitData unit)
        {
            List<ClassAbilityData.enumClassAbilityID> got = unit.gotClassAbilityList;
            if (got == null)
            {
                got = new List<ClassAbilityData.enumClassAbilityID>();
                unit.gotClassAbilityList = got;
            }

            int beforeCount = got.Count;
            int beforePoint = unit.jobLevelUpPoint;

            // 重複・NONE・ツリー外 ID を除去 (取得順は維持)
            var seen = new HashSet<ClassAbilityData.enumClassAbilityID>();
            var cleaned = new List<ClassAbilityData.enumClassAbilityID>();
            foreach (var id in got)
            {
                if (id == ClassAbilityData.enumClassAbilityID.NONE) continue;
                if (!ClassAbilityData.IsVisibleClassAbility(id)) continue;
                if (!seen.Add(id)) continue;
                cleaned.Add(id);
            }

            int jobLevel = unit.jobLevel < 1 ? 1 : unit.jobLevel;
            int point = jobLevel - 1 - cleaned.Count;
            bool refunded = false;
            if (point < 0)
            {
                // 取得数がレベルに見合わない → バニラの ResetJobLevelUpPoint 相当 (全額返還)
                cleaned.Clear();
                point = jobLevel - 1;
                refunded = true;
            }

            bool listChanged = cleaned.Count != got.Count;
            if (listChanged)
            {
                got.Clear();
                got.AddRange(cleaned);
            }
            unit.jobLevelUpPoint = point;

            // 装着スロット: 未取得のものを外し、3枠に揃える
            List<ClassAbilityData.enumClassAbilityID> set = unit.setClassAbilityList;
            if (set == null)
            {
                set = new List<ClassAbilityData.enumClassAbilityID>();
                unit.setClassAbilityList = set;
            }
            bool slotChanged = false;
            for (int i = 0; i < set.Count; i++)
            {
                if (set[i] != ClassAbilityData.enumClassAbilityID.NONE && !got.Contains(set[i]))
                {
                    set[i] = ClassAbilityData.enumClassAbilityID.NONE;
                    slotChanged = true;
                }
            }
            while (set.Count < SlotCount)
            {
                set.Add(ClassAbilityData.enumClassAbilityID.NONE);
                slotChanged = true;
            }

            if (listChanged || beforePoint != point || slotChanged)
            {
                LogHelper.LogWarning("[ClassAbilityRepair] " + unit.unitID
                    + " のクラスアビリティを整合化: 取得数 " + beforeCount + "→" + got.Count
                    + ", 残りポイント " + beforePoint + "→" + point
                    + (refunded ? " (取得数過多のため全額返還)" : "")
                    + (slotChanged ? " (装着スロット補正)" : ""));
            }
        }
    }
}
