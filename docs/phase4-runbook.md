# 阶段 4：两按钮启停 FB 落地运行手册（待博途开启后执行）

## 前置
- TIA Portal V21 开启，加载 `MCP测试项目`
- PLC 名：`PLC_1`
- 现状：只有 `Main`(OB1, LAD, 块号 1)，变量表全空，无 UDT，I/O 地址未被占用

## 逻辑需求
- 启动 `%I0.0`、停止 `%I0.1`、热保护 `%I0.2`
- 输出：接触器 `%Q0.0`、运行指示 `%Q0.1`
- 热保护动作 → 锁定，不自动重启
- 停止优先于启动

## 步骤

### 步骤 1：SCL 已在 artifacts 生成
文件：`C:\PLCMCP\artifacts\FB_MotorStartStop.scl`

逻辑（自写，等效 LAD）：
```
IF NOT ThermalOK THEN TripLatch := TRUE; END_IF;         // 热保护上锁
IF (NOT Stop) OR (NOT ThermalOK) OR TripLatch THEN
    RunLatch := FALSE;                                     // 停止优先/锁定断开
ELSIF Start THEN
    RunLatch := TRUE;                                      // 启动自保持
END_IF;
Contactor := RunLatch;
Running   := RunLatch;
```

首次扫描：`RunLatch`/`TripLatch` 声明时 `:= FALSE`，实例 DB 首次生成即为干净态。

### 步骤 2：dry_run 预演（先给用户看）
```
TiaEngine.exe --mode import_scl --scl C:\PLCMCP\artifacts\FB_MotorStartStop.scl --dry-run
```
预期：`created: [FB_MotorStartStop]`。

### 步骤 3：用户确认后才真导入
```
TiaEngine.exe --mode import_scl --scl C:\PLCMCP\artifacts\FB_MotorStartStop.scl
```

### 步骤 4：编译并读回完整诊断
```
TiaEngine.exe --mode compile_plc
```
若失败：按返回的 `Path` + 完整 `Description` 精确改 SCL，最多 5 轮。

### 步骤 5：OB1 追加调用 —— 需先与用户确认方案
**现状：OB1 是 LAD 语言，不能直接追加 SCL。**

遵守"不改 OB1 原有逻辑，只追加"红线，正确做法是用 SimaticML XML 在 OB1 末尾**追加一个网络**（调用 FB）。这依赖阶段 6 的 LAD 生成器。

方案（**先给用户看，等同意再动**）：
- 导出当前 OB1 为 SimaticML
- 在 XML 末尾追加一个网络：常开触点 `运行使能`（默认 TRUE）→ 调用 `FB_MotorStartStop`（绑定自动生成/显式实例 DB）→ 接线 `%I0.0/%I0.1/%I0.2` → `%Q0.0/%Q0.1`
- 重新导入 OB1（`ImportOptions.Override`）
- 再次编译

风险提示：覆盖导入 OB1 若 XML 不完整会破坏原有逻辑，故必须先导出-确认-追加-对比。

## PLCSIM 验证
- 下载：博途界面手动"下载到设备" → 选 PLCSIM（`S7PLCSIMV21.exe`）
- 变量监控/强制：PLCSIM V21 无进程外读写 API；可用博途"监控表"或 PLCSIM 界面
- 仿真步骤：启动 PLCSIM → 下载 → 监控 %I/%Q → 拨 %I0.0 启动 → 接触器吸合 %Q0.0=1 → 拨 %I0.1 停止 → 断开 %I0.2 热保护 → 锁定