#include "weva/temporal_value.h"
#include <cstdio>

namespace weva {
namespace {
constexpr int64_t day_ms = 86400000;
constexpr int64_t maximum_ms = INT64_C(8640000000000000);
struct Parts { int year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0, milli = 0; };
bool leap(int y) { return y % 4 == 0 && (y % 100 != 0 || y % 400 == 0); }
int64_t year_days(int year) {
    const int64_t y = year - 1;
    return 365 * y + y / 4 - y / 100 + y / 400 - 719162;
}
int month_days(int y, int m) {
    constexpr int lengths[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    return lengths[m - 1] + (m == 2 && leap(y));
}
int64_t date_days(const Parts& p) {
    int64_t days = year_days(p.year) + p.day - 1;
    for (int m = 1; m < p.month; ++m) days += month_days(p.year, m);
    return days;
}
int weekday(int64_t days) { return static_cast<int>(((days + 3) % 7 + 7) % 7); }
bool two(std::string_view s, size_t at, int* value) {
    if (at + 2 > s.size() || s[at] < '0' || s[at] > '9' || s[at + 1] < '0' || s[at + 1] > '9') return false;
    *value = (s[at] - '0') * 10 + s[at + 1] - '0';
    return true;
}
bool year(std::string_view s, size_t* end, Parts* p) {
    size_t at = 0;
    while (at < s.size() && s[at] >= '0' && s[at] <= '9') {
        p->year = p->year * 10 + s[at++] - '0';
        if (p->year > 275760) return false;
    }
    *end = at;
    return at >= 4 && p->year > 0;
}
bool month(std::string_view s, size_t* end, Parts* p) {
    size_t at;
    if (!year(s, &at, p) || at >= s.size() || s[at] != '-' || !two(s, at + 1, &p->month)) return false;
    *end = at + 3;
    return p->month >= 1 && p->month <= 12 && (p->year < 275760 || p->month <= 9);
}
bool date(std::string_view s, Parts* p) {
    size_t at;
    return month(s, &at, p) && s.size() == at + 3 && s[at] == '-' && two(s, at + 1, &p->day) &&
        p->day >= 1 && p->day <= month_days(p->year, p->month) && date_days(*p) * day_ms <= maximum_ms;
}
bool time(std::string_view s, Parts* p) {
    if (s.size() < 5 || !two(s, 0, &p->hour) || s[2] != ':' || !two(s, 3, &p->minute) ||
        p->hour > 23 || p->minute > 59) return false;
    if (s.size() == 5) return true;
    if (s.size() < 8 || s[5] != ':' || !two(s, 6, &p->second) || p->second > 59) return false;
    if (s.size() == 8) return true;
    if (s.size() < 10 || s.size() > 12 || s[8] != '.') return false;
    int scale = 100;
    for (size_t at = 9; at < s.size(); ++at, scale /= 10) {
        if (s[at] < '0' || s[at] > '9') return false;
        p->milli += (s[at] - '0') * scale;
    }
    return true;
}
int64_t time_ms(const Parts& p) { return ((p.hour * 60 + p.minute) * 60 + p.second) * 1000 + p.milli; }
bool parse(std::string_view type, std::string_view s, Parts* p, int64_t* number) {
    if (type == "time") {
        if (!time(s, p)) return false;
        *number = time_ms(*p);
    } else if (type == "date") {
        if (!date(s, p)) return false;
        *number = date_days(*p) * day_ms;
    } else if (type == "month") {
        size_t end;
        if (!month(s, &end, p) || end != s.size()) return false;
        *number = (int64_t(p->year) - 1970) * 12 + p->month - 1;
    } else if (type == "week") {
        size_t at; int week;
        if (!year(s, &at, p) || s.size() != at + 4 || s[at] != '-' || s[at + 1] != 'W' ||
            !two(s, at + 2, &week)) return false;
        const int first = weekday(year_days(p->year));
        const int weeks = 52 + (first == 3 || (first == 2 && leap(p->year)));
        if (week < 1 || week > weeks) return false;
        const int64_t jan4 = year_days(p->year) + 3;
        *number = (jan4 - weekday(jan4) + (week - 1) * 7) * day_ms;
    } else if (type == "datetime-local") {
        const auto split = s.find_first_of("T ");
        if (split == std::string_view::npos || !date(s.substr(0, split), p) || !time(s.substr(split + 1), p)) return false;
        *number = date_days(*p) * day_ms + time_ms(*p);
    } else return false;
    return *number <= maximum_ms;
}
} // namespace
bool is_temporal_type(std::string_view type) {
    return type == "date" || type == "month" || type == "week" || type == "time" || type == "datetime-local";
}
bool parse_temporal_value(std::string_view type, std::string_view value, int64_t* number) {
    Parts p; int64_t parsed;
    if (!parse(type, value, &p, &parsed)) return false;
    if (number) *number = parsed;
    return true;
}
std::string sanitize_temporal_value(std::string_view type, std::string_view value) {
    Parts p; int64_t parsed;
    if (!parse(type, value, &p, &parsed)) return {};
    if (type != "datetime-local") return std::string(value);
    char buffer[48];
    std::snprintf(buffer, sizeof(buffer), "%04d-%02d-%02dT%02d:%02d", p.year, p.month, p.day, p.hour, p.minute);
    std::string result(buffer);
    if (p.second || p.milli) {
        std::snprintf(buffer, sizeof(buffer), ":%02d", p.second);
        result += buffer;
    }
    if (p.milli) {
        std::snprintf(buffer, sizeof(buffer), ".%03d", p.milli);
        result += buffer;
        while (result.back() == '0') result.pop_back();
    }
    return result;
}
} // namespace weva
