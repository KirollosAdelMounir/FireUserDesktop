#import <Foundation/Foundation.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#include <cstdlib>
#include <cstring>

// Standalone ScreenCaptureKit adapter. No content is collected by source discovery.
static SCShareableContent* content() API_AVAILABLE(macos(12.3)) {
    __block SCShareableContent* result = nil;
    dispatch_semaphore_t ready = dispatch_semaphore_create(0);
    [SCShareableContent getShareableContentExcludingDesktopWindows:YES onScreenWindowsOnly:YES completionHandler:^(SCShareableContent* value, NSError* error) {
        if (!error) result = value;
        dispatch_semaphore_signal(ready);
    }];
    if (dispatch_semaphore_wait(ready, dispatch_time(DISPATCH_TIME_NOW, 20 * NSEC_PER_SEC)) != 0) return nil;
    return result;
}

API_AVAILABLE(macos(12.3))
@interface FUShare : NSObject<SCStreamOutput, SCStreamDelegate>
@property(nonatomic, strong) SCStream* stream;
@property(nonatomic, strong) NSCondition* condition;
@property(nonatomic, strong) NSMutableData* frame;
@property(nonatomic) BOOL ended;
@property(nonatomic) BOOL pending;
@property(nonatomic) BOOL stopping;
@end
@implementation FUShare
- (void)stream:(SCStream*)stream didStopWithError:(NSError*)error {
    (void)stream; (void)error;
    [_condition lock]; _ended = YES; [_condition broadcast]; [_condition unlock];
}
- (void)stream:(SCStream*)stream didOutputSampleBuffer:(CMSampleBufferRef)sample ofType:(SCStreamOutputType)type {
    (void)stream;
    if (type != SCStreamOutputTypeScreen || !CMSampleBufferIsValid(sample)) return;
    CFArrayRef attachments = CMSampleBufferGetSampleAttachmentsArray(sample, false);
    if (attachments && CFArrayGetCount(attachments)) {
        NSDictionary* info = (__bridge NSDictionary*)CFArrayGetValueAtIndex(attachments, 0);
        if ([info[SCStreamFrameInfoStatus] integerValue] != SCFrameStatusComplete) return;
    }
    CVPixelBufferRef pixels = CMSampleBufferGetImageBuffer(sample);
    if (!pixels || CVPixelBufferGetWidth(pixels) != 1920 || CVPixelBufferGetHeight(pixels) != 1080) return;
    if (CVPixelBufferLockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly) != kCVReturnSuccess) return;
    [_condition lock];
    if (!_ended) {
        const auto* input = static_cast<const unsigned char*>(CVPixelBufferGetBaseAddress(pixels));
        auto* output = static_cast<unsigned char*>(_frame.mutableBytes);
        const size_t stride = CVPixelBufferGetBytesPerRow(pixels);
        for (size_t y = 0; y < 1080; ++y) {
            const auto* row = input + y * stride; auto* target = output + y * 1920 * 4;
            for (size_t x = 0; x < 1920; ++x) { target[4*x]=row[4*x+2]; target[4*x+1]=row[4*x+1]; target[4*x+2]=row[4*x]; target[4*x+3]=255; }
        }
        _pending = YES; [_condition signal];
    }
    [_condition unlock]; CVPixelBufferUnlockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly);
}
@end

extern "C" __attribute__((visibility("default"))) char* fu_screen_sources() {
    @autoreleasepool {
        if (@available(macOS 12.3, *)) {
            SCShareableContent* items = content(); if (!items) return nullptr;
            NSMutableArray* sources = [NSMutableArray array];
            for (SCDisplay* screen in items.displays) [sources addObject:@{@"id":[NSString stringWithFormat:@"%u", screen.displayID], @"name":[NSString stringWithFormat:@"Screen %lu · %ld × %ld", (unsigned long)sources.count+1, (long)screen.width, (long)screen.height], @"kind":@0, @"backend":@3}];
            for (SCWindow* window in items.windows) {
                if (!window.title.length || window.windowLayer != 0 || window.frame.size.width < 1 || window.frame.size.height < 1) continue;
                NSString* name = window.owningApplication.applicationName.length ? [NSString stringWithFormat:@"%@ — %@", window.owningApplication.applicationName, window.title] : window.title;
                [sources addObject:@{@"id":[NSString stringWithFormat:@"%u",window.windowID], @"name":name, @"kind":@1, @"backend":@3}];
                if (sources.count >= 1024) break;
            }
            NSData* json = [NSJSONSerialization dataWithJSONObject:sources options:0 error:nil];
            return json ? strdup([[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding].UTF8String) : nullptr;
        }
        return nullptr;
    }
}
extern "C" __attribute__((visibility("default"))) void fu_screen_free(void* value) { free(value); }
extern "C" __attribute__((visibility("default"))) void* fu_screen_open(int kind, unsigned long long identifier) {
    @autoreleasepool {
        if (@available(macOS 12.3, *)) {
            SCShareableContent* items = content(); if (!items) return nullptr;
            SCContentFilter* filter = nil;
            if (kind == 0) { for (SCDisplay* display in items.displays) if (display.displayID == identifier) { filter = [[SCContentFilter alloc] initWithDisplay:display excludingWindows:@[]]; break; } }
            else if (kind == 1) { for (SCWindow* window in items.windows) if (window.windowID == identifier) { filter = [[SCContentFilter alloc] initWithDesktopIndependentWindow:window]; break; } }
            if (!filter) return nullptr;
            FUShare* share = [FUShare new]; share.condition = [NSCondition new]; share.frame = [NSMutableData dataWithLength:1920*1080*4];
            SCStreamConfiguration* config = [SCStreamConfiguration new]; config.width = 1920; config.height = 1080; config.scalesToFit = YES; config.minimumFrameInterval = CMTimeMake(1,60); config.queueDepth = 3; config.pixelFormat = kCVPixelFormatType_32BGRA; config.showsCursor = YES;
            if (@available(macOS 14.0, *)) config.preservesAspectRatio = YES;
            share.stream = [[SCStream alloc] initWithFilter:filter configuration:config delegate:share];
            NSError* outputError = nil;
            if (![share.stream addStreamOutput:share type:SCStreamOutputTypeScreen sampleHandlerQueue:dispatch_queue_create("fireuser.screen.frames", DISPATCH_QUEUE_SERIAL) error:&outputError]) return nullptr;
            __block BOOL started = NO; dispatch_semaphore_t ready = dispatch_semaphore_create(0);
            [share.stream startCaptureWithCompletionHandler:^(NSError* error) { started = error == nil; dispatch_semaphore_signal(ready); }];
            if (dispatch_semaphore_wait(ready, dispatch_time(DISPATCH_TIME_NOW, 15*NSEC_PER_SEC)) != 0 || !started) {
                [share.stream stopCaptureWithCompletionHandler:^(NSError* error) { (void)error; }];
                [share.stream removeStreamOutput:share type:SCStreamOutputTypeScreen error:nil]; return nullptr;
            }
            return (__bridge_retained void*)share;
        }
        return nullptr;
    }
}
extern "C" __attribute__((visibility("default"))) int fu_screen_read(void* handle, unsigned char* buffer, int size) {
    @autoreleasepool {
        if (@available(macOS 12.3, *)) {
            if (!handle || !buffer || size != 1920*1080*4) return -1;
            FUShare* share = (__bridge FUShare*)handle;
            [share.condition lock];
            if (!share.ended && !share.pending) [share.condition waitUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.2]];
            int result = share.ended ? -1 : share.pending ? 1 : 0;
            if (result == 1) { memcpy(buffer, share.frame.bytes, (size_t)size); share.pending = NO; }
            [share.condition unlock]; return result;
        }
        return -1;
    }
}
extern "C" __attribute__((visibility("default"))) void fu_screen_stop(void* handle) {
    @autoreleasepool {
        if (@available(macOS 12.3, *)) {
            if (!handle) return; FUShare* share = (__bridge FUShare*)handle;
            [share.condition lock]; BOOL stopping = share.stopping; share.stopping = YES; share.ended = YES; [share.condition broadcast]; [share.condition unlock];
            if (!stopping) {
                dispatch_semaphore_t ready = dispatch_semaphore_create(0);
                [share.stream stopCaptureWithCompletionHandler:^(NSError* error) { (void)error; dispatch_semaphore_signal(ready); }];
                dispatch_semaphore_wait(ready, dispatch_time(DISPATCH_TIME_NOW, 5*NSEC_PER_SEC));
                [share.stream removeStreamOutput:share type:SCStreamOutputTypeScreen error:nil];
            }
        }
    }
}
extern "C" __attribute__((visibility("default"))) void fu_screen_close(void* handle) {
    if (!handle) return; fu_screen_stop(handle); CFRelease(handle);
}
