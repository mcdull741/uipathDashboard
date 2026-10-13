// 全局脚本
(function () {
    // 自动消失提示
    setTimeout(() => {
        document.querySelectorAll('.alert-dismissible').forEach(el => {
            try {
                const instance = bootstrap.Alert.getOrCreateInstance(el);
                instance.close();
            } catch (e) { }
        });
    }, 5000);

    // 日期输入框默认值
    document.querySelectorAll('input[type="datetime-local"]').forEach(input => {
        if (!input.value && input.placeholder) {
            // 留空由用户填写
        }
    });
})();