﻿/*--------------------------------------------------------------------------

Reactor

The MIT License (MIT)

Copyright (c) 2015 Haydn Paterson (sinclair) <haydn.developer@gmail.com>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

---------------------------------------------------------------------------*/

using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Reactor.Tests {
    public static class Extensions {
        public static TaskAwaiter<object> GetAwaiter(this Reactor.Async.Future future) {
            var tcs = new TaskCompletionSource<object>(); 
            future.Then  (() => tcs.SetResult(null));
            future.Error (error => tcs.SetException(error));
            return tcs.Task.GetAwaiter();
        }
        public static TaskAwaiter<T> GetAwaiter<T>(this Reactor.Async.Future<T> future) {
            var tcs = new TaskCompletionSource<T>(); 
            future.Then  (result => tcs.SetResult(result));
            future.Error (error => tcs.SetException(error));
            return tcs.Task.GetAwaiter();
        }
    }
}