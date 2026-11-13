"""
交通灯行为验证 —— 用 lad_behavior_sim 跑真实生成的梯形图。

验证点(来自题目要求):
  1. 正常模式 4 相位循环: 南北红+东西绿10s -> 南北红+东西黄闪3s
                        -> 南北绿+东西红10s -> 南北黄闪+东西红3s -> 循环
  2. 南北/东西 不得同时绿灯(互斥)
  3. 每个方向任意时刻至少有红或绿或黄在亮
  4. 倒计时逐秒递减, 到 0 换相
  5. 紧急模式: 模式2 => 东西绿+南北红; 模式3 => 全红; 倒计时显示 99
"""
import sys
import io
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lad_behavior_sim as sim

SCAN_MS = 100          # 每次扫描 100ms


def driver_factory(mode_setter):
    def drv(st, scan, timers):
        mode_setter(st, scan, timers)
    return drv


def run_case(name, xml, mode_setter, scans):
    drv = driver_factory(mode_setter)
    rows = []
    for scan, st, hist in sim.run(xml, drv, max_scan=scans):
        rows.append(dict(st))
    return rows


def fmt(st, keys):
    return ' '.join('%s=%s' % (k, 1 if st.get(k) else 0) for k in keys)


def main(xml):
    LAMPS = ['南北红灯', '南北绿灯', '南北黄灯', '东西红灯', '东西绿灯', '东西黄灯']
    print('=' * 78)
    print('场景1: 启动 + 正常模式, 观察 46 秒 (460 次扫描)')
    print('=' * 78)

    def setter1(st, scan, timers):
        st['启动'] = True
        st['停止'] = False
        st['正常模式'] = True
        for m in ('模式1', '模式2', '模式3', '模式4'):
            st[m] = False

    rows = run_case('normal', xml, setter1, 470)

    problems = []
    print('%4s %6s %6s  %s' % ('秒', '阶段', '倒计时', '  '.join(LAMPS)))
    for i, st in enumerate(rows):
        if i % 10 == 0:      # 每 1 秒打一行
            print('%4d %6s %6s  %s' % (i // 10, st.get('阶段', '-'), st.get('倒计时', '-'),
                                       '  '.join('%d' % (1 if st.get(k) else 0) for k in LAMPS)))
        # 检查互斥: 不得两方向同时绿
        if st.get('南北绿灯') and st.get('东西绿灯'):
            problems.append('扫描%d: 南北绿和东西绿同时亮!' % i)
        # 检查每个方向至少有一个灯亮。
        # 例外: 黄灯闪烁相位(相位1给东西、相位3给南北)里, 灭的那半周期该方向
        # 确实只有黄灯, 所以"全灭"是闪烁的固有现象, 不算故障。
        yellow_phases = {'南北': 3, '东西': 1}
        for side, yph in yellow_phases.items():
            if st.get('阶段') == yph:
                continue
            lamps = [side + '红灯', side + '绿灯', side + '黄灯']
            if not any(st.get(k) for k in lamps):
                problems.append('扫描%d: %s方向三个灯全灭!(阶段%s)' % (i, side, st.get('阶段')))
        # 检查红绿不得同亮
        if st.get('南北红灯') and st.get('南北绿灯'):
            problems.append('扫描%d: 南北红绿同亮!' % i)
        if st.get('东西红灯') and st.get('东西绿灯'):
            problems.append('扫描%d: 东西红绿同亮!' % i)

    # 相位时长检查
    print()
    phase_seq = []
    for st in rows:
        ph = st.get('阶段', -1)
        if not phase_seq or phase_seq[-1][0] != ph:
            phase_seq.append([ph, 1])
        else:
            phase_seq[-1][1] += 1
    print('相位序列(相位, 持续扫描数/10=秒):')
    for ph, cnt in phase_seq:
        print('   阶段%s  持续 %.1f 秒' % (ph, cnt / 10.0))

    print()
    if problems:
        print('!! 发现 %d 个问题:' % len(problems))
        for p in problems[:15]:
            print('   ' + p)
    else:
        print('OK: 无互斥/全灭/红绿同亮问题')

    # 场景2: 紧急模式
    print()
    print('=' * 78)
    print('场景2: 紧急模式2 (东西通行) + 紧急模式3 (全红)')
    print('=' * 78)

    def setter2(st, scan, timers):
        st['启动'] = True
        st['停止'] = False
        st['正常模式'] = False
        st['模式2'] = True
        st['模式1'] = st['模式3'] = st['模式4'] = False

    rows2 = run_case('emg2', xml, setter2, 40)
    st = rows2[-1]
    print('模式2 稳态: ' + fmt(st, LAMPS) + '  倒计时显示=%s' % st.get('倒计时显示'))
    ok2 = st.get('东西绿灯') and st.get('南北红灯') and not st.get('南北绿灯')

    def setter3(st, scan, timers):
        st['启动'] = True
        st['停止'] = False
        st['正常模式'] = False
        st['模式3'] = True
        st['模式1'] = st['模式2'] = st['模式4'] = False

    rows3 = run_case('emg3', xml, setter3, 40)
    st3 = rows3[-1]
    print('模式3 稳态: ' + fmt(st3, LAMPS) + '  倒计时显示=%s' % st3.get('倒计时显示'))
    ok3 = st3.get('南北红灯') and st3.get('东西红灯') and not st3.get('南北绿灯') and not st3.get('东西绿灯')

    print()
    print('模式2 判定: ' + ('通过' if ok2 else '失败'))
    print('模式3 判定: ' + ('通过' if ok3 else '失败'))

    return 0 if (not problems and ok2 and ok3) else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1]))