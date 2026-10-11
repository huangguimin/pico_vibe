/*
 * Copyright (c) 2026 Pico Vibe contributors.
 *
 * This helper uses the Apple-platform-host technique documented by Jonas van
 * den Berg's BSD-3-Clause MediaRemote Adapter.  macOS 15.4 and newer deny
 * MediaRemote state and commands to ordinary third-party executables, while a
 * small library loaded by Apple's /usr/bin/perl retains the platform host's
 * access.  See THIRD_PARTY_NOTICES.md.
 */

#import <Foundation/Foundation.h>

#include <CoreFoundation/CoreFoundation.h>
#include <dispatch/dispatch.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>

typedef bool (*SendCommandFunction)(int command, id userInfo);
typedef void (^PIDCompletion)(int pid);
typedef void (*GetNowPlayingPIDFunction)(dispatch_queue_t queue,
                                         PIDCompletion completion);

static void failWithCode(int code, const char *message) {
    fprintf(stderr, "%s\n", message);
    fflush(stderr);
    exit(code);
}

static void *loadFunction(CFBundleRef bundle, CFStringRef name) {
    return CFBundleGetFunctionPointerForName(bundle, name);
}

__attribute__((visibility("default"))) void pico_vibe_next_track(void) {
    @autoreleasepool {
        NSURL *url = [NSURL fileURLWithPath:
            @"/System/Library/PrivateFrameworks/MediaRemote.framework"];
        CFBundleRef bundle = CFBundleCreate(kCFAllocatorDefault,
                                            (__bridge CFURLRef)url);
        if (bundle == NULL) {
            failWithCode(3, "MediaRemote framework is unavailable.");
        }

        GetNowPlayingPIDFunction getPID = (GetNowPlayingPIDFunction)loadFunction(
            bundle, CFSTR("MRMediaRemoteGetNowPlayingApplicationPID"));
        SendCommandFunction sendCommand = (SendCommandFunction)loadFunction(
            bundle, CFSTR("MRMediaRemoteSendCommand"));
        if (getPID == NULL || sendCommand == NULL) {
            CFRelease(bundle);
            failWithCode(3, "Required MediaRemote commands are unavailable.");
        }

        __block int activePID = 0;
        dispatch_semaphore_t sessionGate = dispatch_semaphore_create(0);
        dispatch_queue_t queue = dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0);
        getPID(queue, ^(int pid) {
          activePID = pid;
          dispatch_semaphore_signal(sessionGate);
        });
        dispatch_time_t sessionTimeout =
            dispatch_time(DISPATCH_TIME_NOW, 700 * NSEC_PER_MSEC);
        if (dispatch_semaphore_wait(sessionGate, sessionTimeout) != 0) {
            CFRelease(bundle);
            failWithCode(1, "Now Playing session query timed out.");
        }
        if (activePID <= 0) {
            CFRelease(bundle);
            failWithCode(2, "No active macOS Now Playing session is available.");
        }

        if (!sendCommand(4, nil)) {
            CFRelease(bundle);
            failWithCode(4, "The active media player rejected next track.");
        }

        /* Keep the entitled host alive until mediaremoted has consumed the
         * command.  A follow-up request provides a bounded acknowledgement. */
        dispatch_semaphore_t completionGate = dispatch_semaphore_create(0);
        getPID(queue, ^(int pid) {
          (void)pid;
          dispatch_semaphore_signal(completionGate);
        });
        dispatch_time_t completionTimeout =
            dispatch_time(DISPATCH_TIME_NOW, 700 * NSEC_PER_MSEC);
        (void)dispatch_semaphore_wait(completionGate, completionTimeout);
        CFRelease(bundle);
    }
}
