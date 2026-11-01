import { describe, it, expect } from 'vitest';
import { getDateFormatRegion, buildDatePart, formatDate, formatTime, describeExecution, parseDuration } from '../utilities/dateTimeParser';

describe('getDateFormatRegion', () => {
    it('returns "us" for America/ prefixed IANA timezone', () => {
        expect(getDateFormatRegion('America/New_York')).toBe('us');
        expect(getDateFormatRegion('America/Chicago')).toBe('us');
        expect(getDateFormatRegion('America/Los_Angeles')).toBe('us');
    });

    it('returns "us" for US/ prefixed IANA timezone', () => {
        expect(getDateFormatRegion('US/Eastern')).toBe('us');
    });

    it('returns "eu" for Europe/ prefixed IANA timezone', () => {
        expect(getDateFormatRegion('Europe/London')).toBe('eu');
        expect(getDateFormatRegion('Europe/Berlin')).toBe('eu');
    });

    it('returns "eu" for Africa/ prefixed IANA timezone', () => {
        expect(getDateFormatRegion('Africa/Cairo')).toBe('eu');
    });

    it('returns "iso" for undefined or empty timezone', () => {
        expect(getDateFormatRegion(undefined)).toBe('iso');
        expect(getDateFormatRegion('')).toBe('iso');
    });

    it('returns "iso" for unrecognized timezone', () => {
        expect(getDateFormatRegion('Asia/Tokyo')).toBe('iso');
        expect(getDateFormatRegion('UTC')).toBe('iso');
    });

    // Windows timezone ID fallback handling
    it('returns "us" for Windows American timezone IDs', () => {
        expect(getDateFormatRegion('Eastern Standard Time')).toBe('us');
        expect(getDateFormatRegion('Central Standard Time')).toBe('us');
        expect(getDateFormatRegion('Mountain Standard Time')).toBe('us');
        expect(getDateFormatRegion('Pacific Standard Time')).toBe('us');
        expect(getDateFormatRegion('Alaskan Standard Time')).toBe('us');
        expect(getDateFormatRegion('Hawaiian Standard Time')).toBe('us');
    });

    it('returns "eu" for Windows European timezone IDs', () => {
        expect(getDateFormatRegion('W. Europe Standard Time')).toBe('eu');
        expect(getDateFormatRegion('Central European Standard Time')).toBe('eu');
        expect(getDateFormatRegion('E. Europe Standard Time')).toBe('eu');
        expect(getDateFormatRegion('GMT Standard Time')).toBe('eu');
        expect(getDateFormatRegion('Greenwich Standard Time')).toBe('eu');
    });
});

describe('buildDatePart', () => {
    it('builds US format MM/DD/YYYY', () => {
        expect(buildDatePart('us', '2026', '03', '18')).toBe('03/18/2026');
    });

    it('builds EU format DD/MM/YYYY', () => {
        expect(buildDatePart('eu', '2026', '03', '18')).toBe('18/03/2026');
    });

    it('builds ISO format YYYY-MM-DD', () => {
        expect(buildDatePart('iso', '2026', '03', '18')).toBe('2026-03-18');
    });
});

describe('formatDate', () => {
    it('returns empty string for falsy input', () => {
        expect(formatDate('')).toBe('');
        expect(formatDate(null as unknown as string)).toBe('');
    });

    it('formats a UTC date string with IANA timezone', () => {
        const result = formatDate('2026-03-18T04:00:00Z', true, 'America/New_York');
        // Should display as US format (MM/DD/YYYY) in Eastern time
        // 04:00 UTC = 00:00 EST (March 18) or 23:00 EST (March 17) depending on DST
        expect(result).toMatch(/^\d{2}\/\d{2}\/\d{4} \d{2}:\d{2}:\d{2}$/);
    });

    it('formats a UTC date string with Europe timezone', () => {
        const result = formatDate('2026-03-18T12:00:00Z', true, 'Europe/London');
        // Should display as EU format (DD/MM/YYYY)
        expect(result).toMatch(/^\d{2}\/\d{2}\/\d{4} \d{2}:\d{2}:\d{2}$/);
    });

    it('formats date without time when includeTime is false', () => {
        const result = formatDate('2026-03-18T12:00:00Z', false, 'America/New_York');
        expect(result).toMatch(/^\d{2}\/\d{2}\/\d{4}$/);
        expect(result).not.toContain(':');
    });

    it('appends Z to strings without offset for UTC interpretation', () => {
        // A datetime without Z should be treated as UTC
        const withZ = formatDate('2026-03-18T12:00:00Z', false, 'UTC');
        const withoutZ = formatDate('2026-03-18T12:00:00', false, 'UTC');
        expect(withZ).toBe(withoutZ);
    });

    it('gracefully handles invalid timezone by falling back to browser local', () => {
        // Windows timezone IDs are not valid for Intl.DateTimeFormat
        // Should not throw, should still produce a formatted date
        const result = formatDate('2026-03-18T12:00:00Z', true, 'Eastern Standard Time');
        expect(result).toBeTruthy();
        // The fallback uses 'us' region detection for Windows American IDs
        // but the Intl formatter falls back to no timezone, producing a valid date
        expect(result).toMatch(/\d{4}.*\d{2}.*\d{2}/);
    });

    it('handles date boundary correctly for EST timezone', () => {
        // 2026-03-18T03:00:00Z = March 17 at 10pm EST (EST = UTC-5)
        // In March 2026, DST is active (EDT = UTC-4), so this is March 17 at 11pm EDT
        const result = formatDate('2026-03-18T03:00:00Z', false, 'America/New_York');
        // Should show March 17, not March 18
        expect(result).toBe('03/17/2026');
    });

    it('handles date with space separator instead of T', () => {
        const result = formatDate('2026-03-18 12:00:00', false, 'UTC');
        expect(result).toMatch(/2026/);
    });
});

describe('formatTime', () => {
    it('formats milliseconds', () => {
        expect(formatTime(500, true)).toBe('500ms');
    });

    it('formats seconds', () => {
        expect(formatTime(45)).toBe('45s');
    });

    it('formats minutes and seconds', () => {
        expect(formatTime(90)).toBe('1m 30s');
    });

    it('formats hours and minutes', () => {
        expect(formatTime(3660)).toBe('1h 1m');
    });

    it('formats days and hours', () => {
        expect(formatTime(90000)).toBe('1d 1h');
    });

    it('formats exact minutes without seconds', () => {
        expect(formatTime(120)).toBe('2m');
    });

    it('formats exact hours without minutes', () => {
        expect(formatTime(7200)).toBe('2h');
    });

    it('formats exact days without hours', () => {
        expect(formatTime(86400)).toBe('1d');
    });
});

describe('describeExecution', () => {
    const blank = { executedAt: '', executedAgo: '', duration: '' };

    it.each(['0001-01-01T00:00:00', '0001-01-01T00:00:00Z'])('reads as blank for the empty date %s', (value) => {
        expect(describeExecution(value, 0)).toEqual(blank);
    });

    it('reads as blank for an empty Date', () => {
        expect(describeExecution(new Date('0001-01-01T00:00:00Z'), 0)).toEqual(blank);
    });

    it('gives the exact time first, then how long ago and how long it took', () => {
        const recent = new Date(Date.now() - 2 * 60 * 60 * 1000).toISOString();
        const texts = describeExecution(recent, 1500, 'UTC');

        expect(texts.executedAt).toBe(formatDate(recent, true, 'UTC'));
        expect(texts.executedAgo).toMatch(/ago$/);
        expect(texts.duration).toBe(formatTime(1500, true));
    });

    it.each(['Skipped', 7])('reads as blank for a skipped job (%s): it never ran', (status) => {
        const stamped = new Date(Date.now() - 60 * 1000).toISOString();
        expect(describeExecution(stamped, 0, 'UTC', status)).toEqual(blank);
    });

    it('still shows the time of a job that ran', () => {
        const stamped = new Date(Date.now() - 60 * 1000).toISOString();
        expect(describeExecution(stamped, 20, 'UTC', 'Done').executedAt).not.toBe('');
    });

    it('reads a time without an offset as UTC, as the server sends it', () => {
        const texts = describeExecution('2026-11-01T10:00:00.5', 20, 'UTC');

        expect(texts.executedAt).toBe(formatDate('2026-11-01T10:00:00.5', true, 'UTC'));
        expect(texts.executedAt).toContain('10');
    });
});

describe('parseDuration', () => {
    it.each([
        ['30s', 30],
        ['1m', 60],
        ['1m 30s', 90],
        ['2h', 7200],
        ['1h 5m', 3900],
        ['1d 2h', 93600],
    ])('reads %s as %i seconds', (text, seconds) => {
        expect(parseDuration(text)).toBe(seconds);
    });

    it.each(['', 'abc', '30'])('reads %j as zero', (text) => {
        expect(parseDuration(text)).toBe(0);
    });

    it('undoes formatTime for durations under an hour', () => {
        for (const seconds of [1, 30, 59, 60, 90, 600, 3599]) {
            expect(parseDuration(formatTime(seconds))).toBe(seconds);
        }
    });
});
