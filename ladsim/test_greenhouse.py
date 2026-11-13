"""
大棚温度控制行为验证 —— 用 lad_behavior_sim 跑真实生成的梯形图。

题目要求:
  1. 温度>30 亮红灯, <20 亮黄灯, 其余(20~30)亮绿灯
  2. 温度<20 开加热, 每秒+0.2度, 直到 >25 停
  3. 温度>30 开风扇 20 秒
"""
import sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lad_behavior_sim as sim

SCAN_MS = 100   # 每扫描 100ms; 10 扫描 = 1 秒


def run_case(xml, setter, scans):
    rows = []
    for scan, st, hist in sim.run(xml, setter, max_scan=scans):
        rows.append(dict(st))
    return rows


def lamps(st):
    return ('红%d 黄%d 绿%d 加热%d 风扇%d' % (
        1 if st.get('红灯') else 0, 1 if st.get('黄灯') else 0,
        1 if st.get('绿灯') else 0, 1 if st.get('加热电阻丝') else 0,
        1 if st.get('散热风扇') else 0))


def main(xml):
    problems = []

    # ============ 场景1: 从 15 度加热升温 ============
    print('=' * 70)
    print('场景1: 温度从 15 度开始, 观察加热升温过程 (60 秒 = 600 扫描)')
    print('=' * 70)

    def setter1(st, scan, timers):
        st.setdefault('温度', 15.0)

    rows = run_case(xml, setter1, 620)
    print('%5s %6s %s' % ('秒', '温度', '红 黄 绿 加热 风扇'))
    last = None
    for i in range(0, 620, 10):
        st = rows[i]
        print('%5d %6.1f   %s' % (i // 10, st.get('温度', 0), lamps(st)))

    # 断言
    st0 = rows[0]
    if not (st0.get('黄灯') and st0.get('加热电阻丝') and not st0.get('绿灯') and not st0.get('红灯')):
        problems.append('温度15时应 黄灯+加热: %s' % lamps(st0))

    # 温度 20~25 之间(比如 21): 绿灯 + 加热保持
    for st in rows:
        if 20.0 < st.get('温度', 0) <= 25.0:
            if not (st.get('绿灯') and st.get('加热电阻丝')):
                problems.append('温度%.1f 在20~25时应 绿灯+加热: %s' % (st.get('温度', 0), lamps(st)))
            break

    # 温度 > 25: 加热停(容忍一个扫描的过渡态 —— 升温发生在段4, 停加热的判断在段2, 差一个周期)
    max_t = max(st.get('温度', 0) for st in rows)
    print()
    print('  升温到最高 %.2f 度 (理论停加热点 25.2 左右)' % max_t)
    # 稳态检查: 最后 30 个扫描温度稳定在 >25, 加热必须停
    for st in rows[-30:]:
        if st.get('加热电阻丝'):
            problems.append('稳态温度%.1f 加热应停: %s' % (st.get('温度', 0), lamps(st)))
            break

    # ============ 场景2: 高温触发风扇 ============
    print()
    print('=' * 70)
    print('场景2: 温度 35 度(>30), 红灯亮 + 风扇启动')
    print('=' * 70)

    def setter2(st, scan, timers):
        st['温度'] = 35.0

    rows2 = run_case(xml, setter2, 50)
    st2 = rows2[-1]
    print('  稳态: 温度=%.1f  %s' % (st2.get('温度', 0), lamps(st2)))
    if not (st2.get('红灯') and st2.get('散热风扇') and not st2.get('绿灯') and not st2.get('黄灯')):
        problems.append('温度35应 红灯+风扇: %s' % lamps(st2))

    # ============ 场景3: 风扇"至少转20秒" ============
    print()
    print('=' * 70)
    print('场景3: 温度35触发风扇 5 秒后降到 25, 风扇应再转 15 秒(总共20秒)才停')
    print('=' * 70)

    def setter3(st, scan, timers):
        st['温度'] = 35.0 if scan < 50 else 25.0

    rows3 = run_case(xml, setter3, 260)
    # 记录风扇关闭的时刻
    fan_off = None
    for i, st in enumerate(rows3):
        if i >= 50 and not st.get('散热风扇'):
            # 温度降到25后风扇第一次停
            fan_off = i
            break
    print('  温度 5 秒后降到 25; 风扇在第 %.1f 秒(扫描%d)停' % ((fan_off / 10) if fan_off else 0, fan_off or 0))
    if fan_off is None:
        problems.append('风扇在温度降到25后应该停, 但一直没停')
    else:
        # 总运行 = 5s(高温) + 15s(降温后余转) = 20s -> 停在第200扫描附近(20秒)
        dur = fan_off / 10.0
        if not (18.0 <= dur <= 22.0):
            problems.append('风扇总运行 %.1f 秒, 期望约 20 秒' % dur)
        else:
            print('  风扇总运行 %.1f 秒 ≈ 20 秒 ✓' % dur)

    print()
    if problems:
        print('!! 发现 %d 个问题:' % len(problems))
        for p in problems[:20]:
            print('   ' + p)
        return 1
    print('OK: 温度控制逻辑全部符合题目')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1]))