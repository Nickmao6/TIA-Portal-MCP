@echo off
REM 用法: inspect-xml.cmd <file.xml> [--tree|--networks|--wires|--tag X]
REM 设置 UTF-8 输出, 避免中文乱码
setlocal
set PYTHONIOENCODING=utf-8
set "PY=C:\Users\legion\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe"
"%PY%" "%~dp0simaticml_inspect.py" %*
endlocal