#pragma once

#include "pch.h"

namespace droute {

    enum class LogLevel {
        Trace = 0,
        Debug = 1,
        Info  = 2,
        Warn  = 3,
        Error = 4
    };

    const char* LevelToString(LogLevel level);

    namespace Logger {

        bool Init();
        void Shutdown();
        void SetLevel(LogLevel level);

        void Write(LogLevel level, const char* file, int line, const char* fmt, ...);

    }

}

#define LOG_TRACE(fmt, ...) do { droute::Logger::Write(droute::LogLevel::Trace, __FILE__, __LINE__, fmt, ##__VA_ARGS__); } while(0)
#define LOG_DEBUG(fmt, ...) do { droute::Logger::Write(droute::LogLevel::Debug, __FILE__, __LINE__, fmt, ##__VA_ARGS__); } while(0)
#define LOG_INFO(fmt, ...)  do { droute::Logger::Write(droute::LogLevel::Info,  __FILE__, __LINE__, fmt, ##__VA_ARGS__); } while(0)
#define LOG_WARN(fmt, ...)  do { droute::Logger::Write(droute::LogLevel::Warn,  __FILE__, __LINE__, fmt, ##__VA_ARGS__); } while(0)
#define LOG_ERROR(fmt, ...) do { droute::Logger::Write(droute::LogLevel::Error, __FILE__, __LINE__, fmt, ##__VA_ARGS__); } while(0)
