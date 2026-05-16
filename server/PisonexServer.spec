# -*- mode: python ; coding: utf-8 -*-


a = Analysis(
    ['variants\\windows_timer\\windows_service.py'],
    pathex=[],
    binaries=[],
    datas=[('dashboard/templates', 'dashboard/templates'), ('dashboard/static', 'dashboard/static')],
    hiddenimports=['win32timezone', 'servicemanager', 'win32serviceutil', 'win32service'],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='PisonexServer',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='PisonexServer',
)
