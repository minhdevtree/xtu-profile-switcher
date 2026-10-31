// UI text in English (default) and Vietnamese.
// Keys are grouped by area; every key has exactly two strings: { English, Vietnamese }.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace XtuProfileSwitcher
{
    internal static class Loc
    {
        public static string Lang = "en";   // "en" | "vi"

        private static readonly Dictionary<string, string[]> map = new Dictionary<string, string[]>();

        public static bool IsVietnamese { get { return Lang == "vi"; } }

        public static string T(string key)
        {
            string[] v;
            if (!map.TryGetValue(key, out v)) return key;
            return Lang == "vi" ? v[1] : v[0];
        }

        public static string F(string key, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, T(key), args);
        }

        private static void A(string key, string en, string vi) { map[key] = new string[] { en, vi }; }

        static Loc()
        {
            // connection state, top bar
            A("status.connected", "Connected", "Đã kết nối");
            A("status.stopped", "Service stopped", "Dịch vụ đã dừng");
            A("status.offline", "Can't reach XTU", "Không kết nối được XTU");

            // readout
            A("ro.pl1", "PL1 · sustained", "PL1 · dài hạn");
            A("ro.pl2", "PL2 · burst", "PL2 · ngắn hạn");
            A("ro.window", "{0} s window", "Cửa sổ {0} s");
            A("ro.matches", "Matches a saved profile", "Khớp profile đã lưu");
            A("ro.nomatch", "No saved profile matches", "Không khớp profile nào đã lưu");
            A("ro.bios", "BIOS default", "Mặc định BIOS");
            A("ro.off", "off", "tắt");
            A("ro.unavailable", "XTU isn't answering", "XTU không phản hồi");
            A("ro.hint.stopped", "The XTU service is stopped. Use Start XTU service at the bottom.", "Dịch vụ XTU đang dừng. Dùng nút Khởi động dịch vụ XTU ở dưới.");
            A("ro.hint.noanswer", "The service is running but didn't answer. Try again in a moment.", "Dịch vụ đang chạy nhưng không phản hồi. Thử lại sau giây lát.");
            A("power.ac", "Plugged in", "Đang cắm sạc");
            A("power.battery", "On battery", "Đang dùng pin");
            A("power.unknown", "Power source unknown", "Chưa rõ nguồn điện");

            // profile table
            A("pr.title", "Profiles", "Profile");
            A("pr.new", "New profile", "Profile mới");
            A("col.name", "Name", "Tên");
            A("col.pl1", "PL1 W", "PL1 W");
            A("col.pl2", "PL2 W", "PL2 W");
            A("col.window", "Window s", "Cửa sổ s");
            A("col.key", "Hotkey", "Phím tắt");
            A("row.inuse", "In use", "Đang dùng");
            A("row.custom", "custom", "tự tạo");
            A("btn.apply", "Apply", "Áp dụng");
            A("btn.hotkey", "Set hotkey…", "Gán phím tắt…");
            A("btn.delete", "Delete", "Xoá");

            // automatic switch
            A("auto.title", "Switch automatically by power source", "Tự đổi theo nguồn điện");
            A("auto.desc", "Applies a profile when you plug in or unplug.", "Áp dụng profile khi cắm hoặc rút sạc.");
            A("auto.ac", "When plugged in", "Khi cắm sạc");
            A("auto.battery", "On battery", "Khi dùng pin");
            A("auto.none", "Leave as is", "Giữ nguyên");

            // footer
            A("su.title", "Start with Windows", "Mở cùng Windows");
            A("su.desc", "Runs hidden in the tray.", "Chạy ẩn ở khay hệ thống.");
            A("facts.restore", "Restore after restart", "Khôi phục sau khi khởi động lại");
            A("facts.fast", "Fast Startup", "Fast Startup");
            A("on", "On", "Bật");
            A("off", "Off", "Tắt");
            A("btn.startsvc", "Start XTU service", "Khởi động dịch vụ XTU");

            // messages
            A("msg.applied", "Applied: PL1 {0} W · PL2 {1} W · {2} s", "Đã áp dụng: PL1 {0} W · PL2 {1} W · {2} s");
            A("msg.already", "Already in use", "Đang dùng sẵn");
            A("msg.pick", "Pick a profile first.", "Hãy chọn một profile.");
            A("msg.failed", "Couldn't apply {0}. {1}", "Không áp dụng được {0}. {1}");
            A("msg.trayhint", "Still running in the tray. Right-click the icon to quit.", "Vẫn chạy ở khay. Chuột phải biểu tượng để thoát.");
            A("msg.noprofile", "Profile not found", "Không thấy profile");
            A("msg.created", "Created {0}", "Đã tạo {0}");
            A("msg.deleted", "Deleted {0}", "Đã xoá {0}");
            A("msg.onlycustom", "Only profiles made here can be deleted. Delete XTU profiles in XTU.", "Chỉ xoá được profile tạo ở đây. Profile của XTU hãy xoá trong XTU.");
            A("msg.confirmdel", "Delete \"{0}\"?", "Xoá \"{0}\"?");
            A("msg.autoon", "Auto-switch on. Plugged in: {0}. Battery: {1}.", "Đã bật tự đổi. Cắm sạc: {0}. Pin: {1}.");
            A("msg.autooff", "Auto-switch off.", "Đã tắt tự đổi.");
            A("msg.su.on", "Starts with Windows, 15 s after you sign in.", "Sẽ mở cùng Windows, 15 giây sau khi đăng nhập.");
            A("msg.su.off", "Won't start with Windows.", "Sẽ không mở cùng Windows.");
            A("msg.su.fail", "Couldn't change startup. {0}", "Không đổi được mục khởi động. {0}");
            A("msg.svc.ok", "XTU service started.", "Đã khởi động dịch vụ XTU.");
            A("msg.svc.fail", "Couldn't start the service. {0}", "Không khởi động được dịch vụ. {0}");
            A("msg.hk.set", "{1}: hotkey is now {0}.", "{1}: phím tắt là {0}.");
            A("msg.hk.cleared", "{0}: hotkey removed.", "{0}: đã xoá phím tắt.");
            A("msg.hk.invalid", "Not a valid hotkey. Include Ctrl or Alt.", "Phím tắt không hợp lệ. Cần có Ctrl hoặc Alt.");
            A("msg.hk.dup", "{0} is already used by {1}.", "{0} đã dùng cho {1}.");
            A("msg.hk.xtu", "XTU also listens for {0} and would switch silently at the same time. Pick another, or turn off Hotkey-Profile Pairing in XTU.",
                "XTU cũng bắt {0} và sẽ tự đổi cùng lúc mà không báo. Chọn tổ hợp khác, hoặc tắt Hotkey-Profile Pairing trong XTU.");
            A("msg.hk.fail", "Couldn't register {0}. Another app may use it.", "Không đăng ký được {0}. Có thể app khác đang dùng.");
            A("reason.ac", "Plugged in", "Cắm sạc");
            A("reason.battery", "On battery", "Dùng pin");
            A("msg.auto.missing", "Profile \"{0}\" for {1} no longer exists.", "Profile \"{0}\" cho {1} không còn.");

            // errors raised while talking to XTU
            A("err.refused", "XTU refused the connection. Is the XTU service running?", "XTU từ chối kết nối. Dịch vụ XTU có đang chạy không?");
            A("err.notconnected", "Not connected to XTU.", "Chưa kết nối với XTU.");
            A("err.locked", "{0} is locked in XTU.", "{0} đang bị khoá trong XTU.");
            A("err.unsupported", "{0} = {1} W isn't a value XTU accepts.", "{0} = {1} W không phải giá trị XTU chấp nhận.");
            A("err.rejected", "XTU rejected the values ({0}).", "XTU từ chối các giá trị ({0}).");
            A("err.reboot", " A restart is needed.", " Cần khởi động lại.");
            A("err.applyfail", "XTU couldn't apply them ({0}).", "XTU không áp dụng được ({0}).");
            A("val.pl1", "PL1 must be {0}–{1} W.", "PL1 phải từ {0} đến {1} W.");
            A("val.pl2", "PL2 must be between PL1 and {0} W.", "PL2 phải từ PL1 đến {0} W.");
            A("val.window", "Time window must be {0}–{1} s.", "Time window phải từ {0} đến {1} s.");
            A("val.name", "Give it a name of 1–40 characters.", "Đặt tên dài 1–40 ký tự.");
            A("val.namechars", "The name can't start with @ or contain tabs or line breaks.", "Tên không được bắt đầu bằng @ hay chứa tab, xuống dòng.");
            A("val.nameused", "A profile called \"{0}\" already exists.", "Đã có profile tên \"{0}\".");
            A("val.numbers", "Enter whole numbers for the limits.", "Nhập số nguyên cho các giới hạn.");

            // dialogs
            A("dlg.hk.title", "Hotkey for {0}", "Phím tắt cho {0}");
            A("dlg.hk.hint", "Press the new combination. It needs Ctrl or Alt.", "Nhấn tổ hợp mới. Cần có Ctrl hoặc Alt.");
            A("dlg.hk.none", "None", "Chưa gán");
            A("dlg.save", "Save", "Lưu");
            A("dlg.clear", "Clear", "Xoá");
            A("dlg.cancel", "Cancel", "Huỷ");
            A("dlg.create", "Create", "Tạo");
            A("dlg.new.title", "New profile", "Profile mới");
            A("dlg.new.name", "Name", "Tên");
            A("dlg.new.pl1", "PL1 sustained (W)", "PL1 dài hạn (W)");
            A("dlg.new.pl2", "PL2 burst (W)", "PL2 ngắn hạn (W)");
            A("dlg.new.window", "Window (s)", "Cửa sổ (s)");

            // tray
            A("tray.open", "Open", "Mở");
            A("tray.quit", "Quit", "Thoát");
            A("tray.nomatch", "no saved profile", "chưa khớp profile");
            A("tray.offline", "XTU: not connected", "XTU: chưa kết nối");
        }
    }
}
