#Requires AutoHotkey v1.1.36+
#Include %A_ScriptDir%\lib\Event.ahk
#Include %A_ScriptDir%\lib\WaitForSingleObjectAsync.ahk
;==============================================================
; onRegKeyLocalNotify — Registers local registry key change callbacks with asynchronous notification handling
;
; GitHub: https://github.com/SevenKeyboard/on-reg-key-local-notify
; Author: SevenKeyboard Ltd. (2026)
; License: MIT License
;==============================================================

/*
Example Usage:
    callback := func("onScancodeMapNotify")
    onRegKeyLocalNotify("HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Keyboard Layout", callback)
    onScancodeMapNotify(keyName)    {
        ;  ...
    }
*/

class VersionManager_onRegKeyLocalNotify
{
    static _ := VersionManager_onRegKeyLocalNotify._init()
    _init()    {
        global
        ONREGKEYLOCALNOTIFY_VERSION := "1.0.0"
        if (!this._verCheck(EVENT_VERSION, "1.0.0"))
            throw exception("Event version 1.x is required (minimum 1.0.0).")
        if (!this._verCheck(WAITFORSINGLEOBJECTASYNC_VERSION, "1.0.0"))
            throw exception("WaitForSingleObjectAsync version 1.x is required (minimum 1.0.0).")
        return true
    }
    _verCheck(byRef actual, required)    {
        if !isSet(actual)
            return false
        actualMajor     := strSplit(actual, ".",, 2)[1]
        requiredMajor   := strSplit(required, ".",, 2)[1]
        if (actualMajor !== requiredMajor)
            return false
        return verCompare(actual, ">=" required)
    }
}
onRegKeyLocalNotify(keyName, callback, addRemove := 1)    {
    /*
    keyName:
        The full name of the registry key, e.g. HKLM\Software\SomeApplication.
    callback:
        A function name to call (or in [v1.1.20+] a function object). To pass a literal function name, enclose it in quotes.
    addRemove:
        If omitted, the default is 1, which registers the callback.
        Specify 0 to unregister a previously registered callback.
        To register a callback so that it runs before any previously registered ones, specify -1 for addRemove.
    */
    return _RegKeyLocalNotifier.register(keyName, callback, addRemove)
}
class _RegKeyLocalNotifier
{
    _init()    {
        onExit(objBindMethod(this, "_closeRegistryKeysOnExit"))
        this._handles := object()
        this._notifyCallbacks := object()
        this._userCallbacks := object()
    }
    static _ := _RegKeyLocalNotifier._init()

    register(keyName, callback, addRemove)    {
        
        static KEY_NOTIFY := 0x0010
            ,KEY_WOW64_64KEY := 0x0100
            ,KEY_WOW64_32KEY := 0x0200
            ,ERROR_SUCCESS := 0
            ,REG_NOTIFY_CHANGE_LAST_SET := 0x00000004
            ,HKEY_LOCAL_MACHINE     := 0x80000002
            ,HKEY_CURRENT_USER      := 0x80000001
            ,HKEY_CLASSES_ROOT      := 0x80000000
            ,HKEY_CURRENT_CONFIG    := 0x80000005
            
        critical % format("{2}", prevIC := A_IsCritical, "On")
        if (!regExMatch(keyName, "isO)\A("
            . "(*MARK:HKEY_LOCAL_MACHINE)HKEY_LOCAL_MACHINE|HKLM|"
            . "(*MARK:HKEY_CURRENT_USER)HKEY_CURRENT_USER|HKCU|"
            . "(*MARK:HKEY_CLASSES_ROOT)HKEY_CLASSES_ROOT|HKCR|"
            . "(*MARK:HKEY_CURRENT_CONFIG)HKEY_CURRENT_CONFIG|HKCC"
            . ")\\(.+)\z", m))    {
            critical % prevIC
            return false
        }
        rootKey := m.mark()
        subKey := m[2]
        keyName := rootKey "\" subKey
        
        if (!this._notifyCallbacks.hasKey(keyName))
            this._notifyCallbacks[keyName] := objBindMethod(this, "_onKeyNotify", keyName)

        if (addRemove && !this._handles.hasKey(keyName))    {
            areHandlesCreated := false
            this._handles[keyName] := {event:"", waitFunc:"", hKey:""}
            loop 1    {
                this._handles[keyName].event := new Event()
                if (!this._handles[keyName].event.handle)    {
                    this._handles[keyName].event := ""
                    break
                }
                notifyCallback := this._notifyCallbacks[keyName]
                this._handles[keyName].waitFunc := new WaitForSingleObjectAsync(this._handles[keyName].event.handle, notifyCallback)

                samDesired := KEY_NOTIFY
                switch (A_RegView)
                {
                    case 32:    samDesired |= KEY_WOW64_32KEY
                    case 64:    samDesired |= KEY_WOW64_64KEY
                }
                lErrorCode := dllCall("Advapi32.dll\RegOpenKeyEx", "Ptr",%rootKey%, "Str",subKey, "UInt",0, "UInt",samDesired, "Ptr*",hKey, "Int")
                if (lErrorCode !== ERROR_SUCCESS)    {
                    this._handles[keyName].waitFunc := ""
                    this._handles[keyName].event    := ""
                    break
                }
                areHandlesCreated := true
                this._handles[keyName].hKey := hKey
                dllCall("Advapi32.dll\RegNotifyChangeKeyValue"
                    ,"Ptr",this._handles[keyName].hKey
                    ,"Int",false
                    ,"UInt",REG_NOTIFY_CHANGE_LAST_SET
                    ,"Ptr",this._handles[keyName].event.handle
                    ,"Int",true
                    ,"Int")
            }
            if (!areHandlesCreated)    {
                this._handles[keyName] := ""
                this._handles.delete(keyName)
                critical % prevIC
                return false
            }
        }

        newUserCallbacks := []
        if (!this._userCallbacks.hasKey(keyName))
            this._userCallbacks[keyName] := []
        switch (addRemove)
        {
            case 1:
                for _, fn in this._userCallbacks[keyName]    {
                    if (fn !== callback)
                        newUserCallbacks.push(fn)
                }
                newUserCallbacks.push(callback)
            case -1:
                newUserCallbacks.push(callback)
                for _, fn in this._userCallbacks[keyName]    {
                    if (fn !== callback)
                        newUserCallbacks.push(fn)
                }
            case 0:
                for _, fn in this._userCallbacks[keyName]    {
                    if (fn !== callback)
                        newUserCallbacks.push(fn)
                }
        }
        this._userCallbacks[keyName] := newUserCallbacks

        if (!this._userCallbacks[keyName].length())    {
            if (this._handles.hasKey(keyName))    {
                if (hKey := this._handles[keyName].hKey)
                    dllCall("Advapi32.dll\RegCloseKey", "Ptr",hKey, "Int")
                this._handles[keyName].hKey     := ""
                this._handles[keyName].waitFunc := ""
                this._handles[keyName].event    := ""
                this._handles[keyName] := ""
                this._handles.delete(keyName)
            }
        }
        return true
    }

    _onKeyNotify(keyName)    {
        static REG_NOTIFY_CHANGE_LAST_SET := 0x00000004
        if (this._userCallbacks[keyName].length())    {
            for _, fn in this._userCallbacks[keyName]    {
                if (isObject(fn))    {
                    if (fn.call(keyName))
                        break
                }  else if (isFunc(fn))    {
                    if (%fn%(keyName))
                        break
                }
            }
        }
        dllCall("Advapi32.dll\RegNotifyChangeKeyValue"
            ,"Ptr",this._handles[keyName].hKey
            ,"Int",false
            ,"UInt",REG_NOTIFY_CHANGE_LAST_SET
            ,"Ptr",this._handles[keyName].event.handle
            ,"Int",true
            ,"Int")
    }

    _closeRegistryKeysOnExit(exitReason, exitCode)    {
        for keyName in this._handles    {
            if (hKey := this._handles[keyName].hKey)
                dllCall("Advapi32.dll\RegCloseKey", "Ptr",hKey, "Int")
            this._handles[keyName].hKey     := ""
            this._handles[keyName].waitFunc := ""
            this._handles[keyName].event    := ""
        }
    }
}
