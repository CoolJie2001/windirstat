// wds_core_api.h - C ABI contract between wdscore.dll (trimmed from upstream
// WinDirStat C++ core) and the .NET shell (WdsShell.Interop).
//
// Design rules (this is what keeps upstream sync cheap - violate them and the
// fork's maintenance model collapses):
//   1. PUSH model only: the core streams scan events; the shell owns the tree
//      data structure. No CItem pointers/ids ever cross the boundary. Upstream
//      may refactor Item.h/CTreeListItem freely as long as "who scanned what"
//      semantics survive.
//   2. Fixed-size POD event payload, utf-16 wide strings, cdecl.
//      Append-only evolution: new fields ONLY at the end, new kinds ONLY via
//      WdsEventKind extension; never renumber.
//   3. The surface below is the WHOLE contract. Anything the shell needs that
//      is not here should be implemented shell-side (C#), not added here.
//
// SPDX-License-Identifier: GPL-2.0-or-later (matches upstream WinDirStat)

#pragma once

#include <stdint.h>
#include <stdbool.h>
#include <wchar.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef WDSCORE_EXPORTS
#define WDS_API __declspec(dllexport)
#else
#define WDS_API __declspec(dllimport)
#endif

// ---------------------------------------------------------------------------
// Versioning
// ---------------------------------------------------------------------------

// ABI version. Shell refuses to load if mismatched. Bump minor on appended
// fields/kinds, major on any breaking change (should be never by rule 2).
#define WDS_ABI_VERSION 0x00010000u // 1.0

WDS_API uint32_t wds_api_version(void);

// ---------------------------------------------------------------------------
// Handles & lifecycle
// ---------------------------------------------------------------------------

typedef struct WdsCore* WdsHandle;

typedef struct WdsOptions
{
    uint32_t structSize;     // sizeof(WdsOptions) - forward-compat guard
    uint32_t workerThreads;  // 0 = auto
    bool     preferMft;      // try FinderNtfs ($MFT direct read), fallback basic
    bool     includeFreeSpace; // emit pseudo entries for <Free Space>
} WdsOptions;

// Returns NULL on failure (e.g. called on unsupported OS).
WDS_API WdsHandle wds_create(const WdsOptions* options /* may be NULL */);
WDS_API void wds_destroy(WdsHandle handle);

// ---------------------------------------------------------------------------
// Events (push protocol)
// ---------------------------------------------------------------------------

typedef enum WdsEventKind
{
    WDS_EV_SCAN_STARTED   = 1, // root fields valid
    WDS_EV_DIR            = 2, // dirToken/parentToken/name valid
    WDS_EV_FILE           = 3, // parentToken/name/logical/physical valid
    WDS_EV_PROGRESS       = 4, // scannedBytes/fileCount valid (throttled by core)
    WDS_EV_EXTENSION      = 5, // extName/bytes valid (throttled by core)
    WDS_EV_SCAN_STATE     = 6, // state = WdsScanState
} WdsEventKind;

typedef enum WdsScanState
{
    WDS_STATE_RUNNING   = 1,
    WDS_STATE_PAUSED    = 2,
    WDS_STATE_COMPLETED = 3,
    WDS_STATE_STOPPED   = 4,
    WDS_STATE_FAILED    = 5,
} WdsScanState;

// Fixed-size POD. Strings are UTF-16, NOT null-terminated; length in units.
// Pointers are only valid for the duration of the callback (copy to keep).
typedef struct WdsEvent
{
    uint32_t     size;        // sizeof(WdsEvent) - receiver must sanity-check
    uint32_t     kind;        // WdsEventKind
    uint64_t     rootToken;   // identifies this scan generation
    uint64_t     nodeToken;   // DIR: this dir; FILE: unused
    uint64_t     parentToken; // dir this entry belongs to
    const wchar_t* name;
    uint32_t     nameLength;
    uint64_t     logicalSize;
    uint64_t     physicalSize;
    uint32_t     attributes;  // FILE_ATTRIBUTE_* passthrough
    uint32_t     state;       // WdsScanState for SCAN_STATE
    const wchar_t* extName;   // EXTENSION events
    uint32_t     extNameLength;
    uint64_t     scannedBytes; // PROGRESS events
    uint64_t     fileCount;    // PROGRESS events
} WdsEvent;

typedef void (*WdsEventCallback)(void* context, const WdsEvent* ev);

// Callback may fire on any core worker thread; the shell must marshal.
// Only one callback can be registered; re-registering replaces it.
WDS_API int wds_set_callback(WdsHandle handle, WdsEventCallback cb, void* context);

// ---------------------------------------------------------------------------
// Scan control (mirrors CWinDirStatModel::StartScanningEngine et al.)
// ---------------------------------------------------------------------------

// pathSpec: e.g. L"C:\\" or a folder path. Returns 0 on success,
// nonzero error code otherwise (scan errors arrive via SCAN_STATE).
WDS_API int wds_scan_start(WdsHandle handle, const wchar_t* pathSpec);
WDS_API int wds_scan_stop(WdsHandle handle);     // user-requested stop
WDS_API int wds_scan_abort(WdsHandle handle);    // discard results
WDS_API int wds_scan_suspend(WdsHandle handle, bool suspend);
WDS_API bool wds_scan_is_running(WdsHandle handle);

#ifdef __cplusplus
} // extern "C"
#endif
