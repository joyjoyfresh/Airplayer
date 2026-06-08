---
description: "依据项目编码规范，为选中的 Python 代码自动添加注释。"
name: "add-comment"
agent: "agent"
---

# 代码注释添加规则

1. 原则上，尽量为每一行代码添加中文注释。
2. 注释内容要简洁、准确，直接说明该行代码的作用。
3. 所有注释统一使用中文，不使用英文注释。
4. 若某行是空行、括号行或语法分隔行，也要通过相邻注释说明其结构用途。
5. 在不破坏语法的前提下，优先使用行内注释；若语言不支持行内注释，则使用上一行中文注释。
6. 定义函数时，函数定义行可以只写用途注释，参数说明优先放在 docstring 中统一说明参数、返回值和用途。

示例（Python，普通代码）：

```python
x = 10  # 定义变量x并赋值为10
y = 20  # 定义变量y并赋值为20
z = x + y  # 计算x与y的和并赋值给z
print(z)  # 输出z的值
```

示例（Python，函数定义）：

```python
def __init__(self, scaler, fit_scaler=False):  # 定义初始化方法
	"""
	scaler: DataScaler 实例
	fit_scaler: 是否在当前数据上拟合归一化器
	"""
	if fit_scaler:  # 判断是否需要拟合归一化器
		self.scaler = scaler  # 保存归一化器实例
	else:  # 处理不拟合归一化器的情况
		self.scaler = scaler  # 直接保存归一化器实例
```
