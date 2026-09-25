// C uses an out pointer for errors; these wrappers expose Wasm multi-value returns.
.globaltype __stack_pointer, i32
.globl dotnetisolator_invoke_scalar_multi
.functype dotnetisolator_invoke_scalar (i32, i32, i64, i32, i32, i32) -> (i64)
dotnetisolator_invoke_scalar_multi:
.functype dotnetisolator_invoke_scalar_multi (i32, i32, i64, i32, i32) -> (i64, i32)
.local i32, i64, i32
 global.get __stack_pointer
 i32.const 16
 i32.sub
 local.tee 5
 global.set __stack_pointer
 local.get 0
 local.get 1
 local.get 2
 local.get 3
 local.get 4
 local.get 5
 call dotnetisolator_invoke_scalar
 local.set 6
 local.get 5
 i32.load 0
 local.set 7
 local.get 5
 i32.const 16
 i32.add
 global.set __stack_pointer
 local.get 6
 local.get 7
 end_function

.globl dotnetisolator_invoke_scalar_2_multi
.functype dotnetisolator_invoke_scalar_2 (i32, i32, i64, i64, i32, i32, i32) -> (i64)
dotnetisolator_invoke_scalar_2_multi:
.functype dotnetisolator_invoke_scalar_2_multi (i32, i32, i64, i64, i32, i32) -> (i64, i32)
.local i32, i64, i32
 global.get __stack_pointer
 i32.const 16
 i32.sub
 local.tee 6
 global.set __stack_pointer
 local.get 0
 local.get 1
 local.get 2
 local.get 3
 local.get 4
 local.get 5
 local.get 6
 call dotnetisolator_invoke_scalar_2
 local.set 7
 local.get 6
 i32.load 0
 local.set 8
 local.get 6
 i32.const 16
 i32.add
 global.set __stack_pointer
 local.get 7
 local.get 8
 end_function

.globl dotnetisolator_invoke_scalar_3_multi
.functype dotnetisolator_invoke_scalar_3 (i32, i32, i64, i64, i64, i32, i32, i32) -> (i64)
dotnetisolator_invoke_scalar_3_multi:
.functype dotnetisolator_invoke_scalar_3_multi (i32, i32, i64, i64, i64, i32, i32) -> (i64, i32)
.local i32, i64, i32
 global.get __stack_pointer
 i32.const 16
 i32.sub
 local.tee 7
 global.set __stack_pointer
 local.get 0
 local.get 1
 local.get 2
 local.get 3
 local.get 4
 local.get 5
 local.get 6
 local.get 7
 call dotnetisolator_invoke_scalar_3
 local.set 8
 local.get 7
 i32.load 0
 local.set 9
 local.get 7
 i32.const 16
 i32.add
 global.set __stack_pointer
 local.get 8
 local.get 9
 end_function

.globl dotnetisolator_invoke_scalar_4_multi
.functype dotnetisolator_invoke_scalar_4 (i32, i32, i64, i64, i64, i64, i32, i32, i32) -> (i64)
dotnetisolator_invoke_scalar_4_multi:
.functype dotnetisolator_invoke_scalar_4_multi (i32, i32, i64, i64, i64, i64, i32, i32) -> (i64, i32)
.local i32, i64, i32
 global.get __stack_pointer
 i32.const 16
 i32.sub
 local.tee 8
 global.set __stack_pointer
 local.get 0
 local.get 1
 local.get 2
 local.get 3
 local.get 4
 local.get 5
 local.get 6
 local.get 7
 local.get 8
 call dotnetisolator_invoke_scalar_4
 local.set 9
 local.get 8
 i32.load 0
 local.set 10
 local.get 8
 i32.const 16
 i32.add
 global.set __stack_pointer
 local.get 9
 local.get 10
 end_function

