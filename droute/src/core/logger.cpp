#include "pch.h"
#include "src/core/logger.hpp"
#include "src/core/config.hpp"

#include <cstdarg>
#include <ctime>

namespace droute {

    static HANDLE g_logFile = INVALID_HANDLE_VALUE;
    static std::mutex g_logMutex;
    static std::atomic<LogLevel> g_logLevel{ LogLevel::Info };
    static constexpr LONGLONG MAX_LOG_FILE_SIZE = 2LL * 1024 * 1024;

    const char* LevelToString(LogLevel level) {
        switch (level) {
            case LogLevel::Trace: return "TRACE";
            case LogLevel::Debug: return "DEBUG";
            case LogLevel::Info:  return "INFO";
            case LogLevel::Warn:  return "WARN";
            case LogLevel::Error: return "ERROR";
        }
        return "?????";
    }

    bool Logger::Init() {
        char path[MAX_PATH];
        DWORD len = GetTempPathA(MAX_PATH, path);
        if (len == 0 || len >= MAX_PATH) {
            path[0] = '.'; path[1] = '\\'; path[2] = '\0';
        }
        strcat_s(path, MAX_PATH, "droute.log");

        HANDLE existing = CreateFileA(path, GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        if (existing != INVALID_HANDLE_VALUE) {
            LARGE_INTEGER size = {};
            const bool shouldRotate = GetFileSizeEx(existing, &size) && size.QuadPart >= MAX_LOG_FILE_SIZE;
            CloseHandle(existing);
            if (shouldRotate) {
                const std::string backupPath = std::string(path) + ".1";
                if (!MoveFileExA(path, backupPath.c_str(), MOVEFILE_REPLACE_EXISTING)) {
                    char msg[256];
                    snprintf(msg, sizeof(msg), "droute: failed to rotate log '%s': %lu\n", path, GetLastError());
                    OutputDebugStringA(msg);
                }
            }
        }

        HANDLE hFile = CreateFileA(path,
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            NULL,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            NULL);

        if (hFile == INVALID_HANDLE_VALUE) {
            char msg[256];
            snprintf(msg, sizeof(msg), "droute: failed to open log file '%s'\n", path);
            OutputDebugStringA(msg);
            g_logFile = INVALID_HANDLE_VALUE;
            return false;
        }

        SetFilePointer(hFile, 0, NULL, FILE_END);

        g_logFile = hFile;

        SYSTEMTIME st;
        GetLocalTime(&st);
        char exePath[MAX_PATH] = {};
        GetModuleFileNameA(NULL, exePath, MAX_PATH);
        char header[512];
        int headerLen = snprintf(header, sizeof(header),
            "// droute session started at %04d-%02d-%02d %02d:%02d:%02d.%03d; pid=%lu; process=%s\n",
            st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
            GetCurrentProcessId(), exePath);

        if (headerLen > 0) {
            DWORD written;
            DWORD length = static_cast<DWORD>(min(headerLen, static_cast<int>(sizeof(header) - 1)));
            WriteFile(g_logFile, header, length, &written, NULL);
        }

        return true;
    }

    void Logger::Shutdown() {
        std::lock_guard<std::mutex> lock(g_logMutex);
        if (g_logFile != INVALID_HANDLE_VALUE) {
            CloseHandle(g_logFile);
            g_logFile = INVALID_HANDLE_VALUE;
        }
    }

    void Logger::SetLevel(LogLevel level) {
        g_logLevel.store(level, std::memory_order_relaxed);
    }

    void Logger::Write(LogLevel level, const char* file, int line, const char* fmt, ...) {
        if (level < g_logLevel.load(std::memory_order_relaxed)) return;

        const int socketError = WSAGetLastError();

        const char* basename = file;
        const char* p = strrchr(file, '\\');
        if (p) basename = p + 1;
        p = strrchr(file, '/');
        if (p && p + 1 > basename) basename = p + 1;

        SYSTEMTIME st;
        GetLocalTime(&st);

        va_list args;
        va_start(args, fmt);

        char msg[2048];
        vsnprintf_s(msg, sizeof(msg), _TRUNCATE, fmt, args);
        va_end(args);

        char buf[2300];
        int bufLen = snprintf(buf, sizeof(buf),
            "[%04d-%02d-%02d %02d:%02d:%02d.%03d] [PID:%lu] [TID:%lu] [%s] [%s:%d] %s\n",
            st.wYear, st.wMonth, st.wDay,
            st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
            GetCurrentProcessId(),
            GetCurrentThreadId(),
            LevelToString(level),
            basename, line,
            msg);

        {
            std::lock_guard<std::mutex> lock(g_logMutex);
            if (g_logFile != INVALID_HANDLE_VALUE) {
                DWORD written;
                if (bufLen > 0) {
                    DWORD length = static_cast<DWORD>(min(bufLen, static_cast<int>(sizeof(buf) - 1)));
                    if (!WriteFile(g_logFile, buf, length, &written, NULL) || written != length)
                        OutputDebugStringA(buf);
                }
            } else {
                OutputDebugStringA(buf);
            }
        }

        WSASetLastError(socketError);
    }

}
