#import <Foundation/Foundation.h>
#import <UserNotifications/UserNotifications.h>

typedef void (*fu_notification_callback)(const char* sender);
static fu_notification_callback activation;

@interface FireUserNotificationDelegate : NSObject <UNUserNotificationCenterDelegate>
@end
@implementation FireUserNotificationDelegate
- (void)userNotificationCenter:(UNUserNotificationCenter*)center
    didReceiveNotificationResponse:(UNNotificationResponse*)response
    withCompletionHandler:(void (^)(void))completion {
    NSString* sender = response.notification.request.content.userInfo[@"sender"];
    if (activation && sender) activation(sender.UTF8String);
    completion();
}
@end

static FireUserNotificationDelegate* notificationDelegate;

extern "C" __attribute__((visibility("default"))) void fu_notifications_initialize(fu_notification_callback callback) {
    activation = callback;
    if (!NSBundle.mainBundle.bundleIdentifier) return;
    notificationDelegate = [FireUserNotificationDelegate new];
    UNUserNotificationCenter.currentNotificationCenter.delegate = notificationDelegate;
}

extern "C" __attribute__((visibility("default"))) int fu_notifications_show(const char* title, const char* body, const char* sender) {
    if (!NSBundle.mainBundle.bundleIdentifier || !title || !body || !sender) return 0;
    @autoreleasepool {
        UNMutableNotificationContent* content = [UNMutableNotificationContent new];
        content.title = [NSString stringWithUTF8String:title];
        content.body = [NSString stringWithUTF8String:body];
        // The managed native audio player uses the Angular notification tone.
        content.userInfo = @{@"sender": [NSString stringWithUTF8String:sender]};
        UNUserNotificationCenter* center = UNUserNotificationCenter.currentNotificationCenter;
        [center requestAuthorizationWithOptions:UNAuthorizationOptionAlert | UNAuthorizationOptionSound
            completionHandler:^(BOOL granted, NSError* error) {
                if (!granted || error) return;
                UNNotificationRequest* request = [UNNotificationRequest requestWithIdentifier:content.userInfo[@"sender"] content:content trigger:nil];
                [center addNotificationRequest:request withCompletionHandler:nil];
            }];
    }
    return 1;
}
