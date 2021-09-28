# Trajectory-visualizer

## Краткое описание
В игре присутствует собственная физика, симулирующая работу видоизмененного закона всемирного тяготения, совмещенная с движком 2D физики Unity. Было необходимо в Edit-time рассчитывать траектории полётов тел в зависимсоти от их начального положения, вектора начальной скорости и других параметров физической системы. Для решения задачи была изучена «гравитационная задача». В результате для оптимизации работы алгоритма и исключения блокирования интерфейса редактора Unity использовалась многопоточность.


## Функционал
* Демонстрация траектории как одного тела, так и всей системы
* Демонстрация точек перечения тел
* Редактирование длительности рассчитываемого периода времени

## Пример работы
